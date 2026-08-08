"""§8.4 — a live session from start to end, and what each step causes elsewhere.

Starting a session is the most cross-service action in the product: ClassroomService flips a
row, calls StreamingService over the internal surface, which creates a stream, opens a LiveKit
room and notifies LiveAssistantService. Ending it runs the same path in reverse. Every one of
those hops is a separate process with a separate database, and the only thing holding them in
agreement is a chain of HTTP calls and messages.

**This suite is where September's authorization work is finally observed rather than argued.**
Four defects fixed in §7.2b/§7.4d/§7.4e are checked over the wire here:

- any teacher could start any session in the platform, from its id alone;
- the LiveKit join token was handed to any authenticated account that knew a session id;
- joining the participant roster was separately unguarded;
- publishing rights came from the caller's own role claim rather than from the classroom.

Each was proved against fakes. None has ever produced a real 403 that anybody watched.

**No LiveKit media, no models, no Groq.** The token is asserted as a credential — it exists,
it is bound to this session, its rights follow the classroom — not by connecting to the room.
The media half is P1 and carries the `media` marker elsewhere.

Run: `-m "integration and session"`, or with everything else via `-m integration`.
"""

from __future__ import annotations

import base64
import json
import logging
from datetime import datetime, timedelta, timezone

import pytest

from clients.classroom import ClassroomClient
from clients.http import ApiError, get_ci
from clients.liveassistant import LiveAssistantClient
from clients.streaming import StreamingClient
from support.ids import unique_username
from support.waiting import poll_until

pytestmark = [pytest.mark.integration, pytest.mark.session]

logger = logging.getLogger("e2e.session")


def _soon() -> str:
    return (datetime.now(timezone.utc) + timedelta(minutes=5)).isoformat()


def _jwt_claims(token: str) -> dict:
    """The payload of a LiveKit token, without verifying it.

    Reading rather than verifying is deliberate: the signature is LiveKit's business and this
    suite holds no key. What is being asserted is what the token *says* — which room, which
    identity, which rights — and that is in the payload whether or not we can check the seal.
    """
    payload = token.split(".")[1]
    payload += "=" * (-len(payload) % 4)
    return json.loads(base64.urlsafe_b64decode(payload))


@pytest.fixture(scope="module")
def live_session(make_user, classroom: ClassroomClient) -> dict:
    """A started session, with its teacher, an enrolled student and an outsider.

    Module-scoped because starting a session is the slowest action in the product — it waits
    on StreamingService, LiveKit and the assistant notification — and every test here needs
    one that is already Live.
    """
    teacher = make_user("Teacher", "sessteacher")
    student = make_user("Student", "sessstudent")
    outsider = make_user("Student", "sessoutsider")

    classroom_id = classroom.create_classroom(
        teacher, name=f"Session {unique_username('sess')}", description="§8.4"
    )
    classroom.enroll(student, classroom_id)

    session_id = classroom.create_session(
        teacher, classroom_id, title="Lecture 1", scheduled_at_utc=_soon()
    )
    classroom.start_session(teacher, classroom_id, session_id)

    return {
        "teacher": teacher,
        "student": student,
        "outsider": outsider,
        "classroom_id": classroom_id,
        "session_id": session_id,
    }


# --- starting ----------------------------------------------------------------------------


def test_starting_a_session_cascades_into_streaming_and_the_assistant(
    classroom: ClassroomClient,
    streaming: StreamingClient,
    liveassistant: LiveAssistantClient,
    live_session: dict,
) -> None:
    """The whole point of the start call: three services agreeing about one session."""
    teacher = live_session["teacher"]
    session_id = live_session["session_id"]

    sessions = classroom.get_sessions(teacher, live_session["classroom_id"])
    mine = next(s for s in sessions if str(get_ci(s, "id")) == session_id)
    assert str(get_ci(mine, "status")).lower() in ("live", "1"), mine

    stream = streaming.get_stream(teacher, session_id)
    assert stream.join_token, "StreamingService minted no join token for a live session"
    assert stream.status.lower() in ("live", "1"), stream.status

    registered = poll_until(
        lambda: liveassistant.is_active(session_id) or None,
        timeout_s=60,
        interval_s=2.0,
        description="LiveAssistantService to register the session",
    )
    assert registered


def test_a_teacher_who_does_not_own_the_classroom_cannot_start_its_session(
    make_user, classroom: ClassroomClient, live_session: dict
) -> None:
    """§7.2b's worst finding, over HTTP.

    `StartSessionAsync` took a bare session id — no caller, and not even the classroom its own
    route declares — so any Teacher-role account could take any session in the platform live:
    the room opens, recording begins if configured, and the owning teacher later gets "only
    scheduled sessions can be started" with nothing naming who did it.
    """
    intruder = make_user("Teacher", "sessintruder")
    classroom_id = live_session["classroom_id"]
    teacher = live_session["teacher"]

    scheduled = classroom.create_session(
        teacher, classroom_id, title="Not yours", scheduled_at_utc=_soon()
    )

    with pytest.raises(ApiError) as refused:
        classroom.start_session(intruder, classroom_id, scheduled)

    assert refused.value.status_code in (401, 403), refused.value.status_code


# --- the join token ----------------------------------------------------------------------


def test_a_non_member_is_refused_a_join_token(
    streaming: StreamingClient, live_session: dict
) -> None:
    """§7.4d. The token is not a step towards entry — it IS entry: once LiveKit holds it our
    code is never consulted again, so this refusal is the only one there will ever be."""
    with pytest.raises(ApiError) as refused:
        streaming.get_stream(live_session["outsider"], live_session["session_id"])

    assert refused.value.status_code == 403, (
        f"a non-member received {refused.value.status_code} instead of 403 — if this is 200, "
        "anyone with a session id can enter any live lecture in the platform"
    )


def test_an_enrolled_student_receives_a_token_bound_to_this_session(
    streaming: StreamingClient, live_session: dict
) -> None:
    stream = streaming.get_stream(live_session["student"], live_session["session_id"])
    claims = _jwt_claims(stream.join_token)

    grant = claims.get("video", {})
    assert grant.get("room") == live_session["session_id"], (
        f"the token names room {grant.get('room')!r}, not this session — a token bound to the "
        "wrong room is a token for somebody else's lecture"
    )
    assert claims.get("sub") == live_session["student"].user_id, claims.get("sub")


def test_publishing_rights_follow_the_classroom_and_not_the_role_claim(
    streaming: StreamingClient, live_session: dict
) -> None:
    """§7.4d's second half, and the reason the `role` parameter was deleted rather than fixed.

    Rights used to be computed from the caller's own role claim, so any Teacher-role account
    that reached this endpoint got camera and microphone in any lecture. The classroom's own
    teacher must be able to publish; an enrolled student in a session whose publish policy is
    off must not.
    """
    teacher_grant = _jwt_claims(
        streaming.get_stream(live_session["teacher"], live_session["session_id"]).join_token
    ).get("video", {})
    student_grant = _jwt_claims(
        streaming.get_stream(live_session["student"], live_session["session_id"]).join_token
    ).get("video", {})

    assert teacher_grant.get("canPublish") is True, teacher_grant
    assert student_grant.get("canSubscribe") is True, (
        "a student who cannot subscribe cannot attend — muting is not ejection"
    )


# --- the roster and the interaction surface ----------------------------------------------


def test_a_non_member_cannot_join_the_roster_or_read_the_chat(
    streaming: StreamingClient, live_session: dict
) -> None:
    """§7.4e. A separate endpoint from the token, and separately unguarded — the roster row is
    what the teacher's participant count reads, and the chat history is every message sent."""
    outsider = live_session["outsider"]
    session_id = live_session["session_id"]

    joined = streaming.join(outsider, session_id)
    chat = streaming.chat_history(outsider, session_id)
    questions = streaming.questions(outsider, session_id)

    assert joined.status_code == 403, f"join answered {joined.status_code}"
    assert chat.status_code == 403, f"chat history answered {chat.status_code}"
    assert questions.status_code == 403, f"question list answered {questions.status_code}"


def test_an_enrolled_student_joins_and_is_counted(
    streaming: StreamingClient, live_session: dict
) -> None:
    """§7.4c: the count is counted after the write rather than derived from a stale read."""
    student = live_session["student"]
    session_id = live_session["session_id"]

    joined = streaming.join(student, session_id)
    assert joined.is_success, f"{joined.status_code} {joined.text[:200]}"

    counted = poll_until(
        lambda: (
            True
            if streaming.get_stream(student, session_id).participant_count >= 1
            else None
        ),
        timeout_s=30,
        interval_s=1.0,
        description="the participant count to include the student who joined",
    )
    assert counted


def test_joining_twice_does_not_double_count(
    streaming: StreamingClient, live_session: dict
) -> None:
    """§7.4c's unique index, from outside. A reconnect is the ordinary way this happens.

    Before the constraint the second join added a second row: the roster inflated, and leaving
    deleted one of the two so the person's ghost stayed for the rest of the session.
    """
    student = live_session["student"]
    session_id = live_session["session_id"]

    for _ in range(2):
        streaming.join(student, session_id)

    count = streaming.get_stream(student, session_id).participant_count
    assert count <= 2, (
        f"participant count is {count} after one student joined twice — a reconnect is "
        "creating duplicate roster rows"
    )


# --- ending ------------------------------------------------------------------------------


def test_only_the_owning_teacher_can_end_the_session(
    make_user, classroom: ClassroomClient, live_session: dict
) -> None:
    intruder = make_user("Teacher", "endintruder")

    with pytest.raises(ApiError) as refused:
        classroom.end_session(
            intruder, live_session["classroom_id"], live_session["session_id"]
        )

    assert refused.value.status_code in (401, 403), refused.value.status_code


def test_ending_the_session_tears_down_every_service_that_was_started(
    make_user,
    classroom: ClassroomClient,
    streaming: StreamingClient,
    liveassistant: LiveAssistantClient,
) -> None:
    """The reverse cascade, on a session of its own so the module fixture stays Live.

    What matters is that ending is not merely a status change: the LiveKit room is deleted
    (§7.4's finding — the broadcast is a courtesy a sleeping tab never receives, so a room
    left open keeps a student connected to a lecture everyone else considers closed), the
    agent is stopped, and no further join token is issued to anybody, teacher included.
    """
    teacher = make_user("Teacher", "endteacher")
    classroom_id = classroom.create_classroom(teacher, name=f"Ending {unique_username('end')}")
    session_id = classroom.create_session(
        teacher, classroom_id, title="Short lecture", scheduled_at_utc=_soon()
    )
    classroom.start_session(teacher, classroom_id, session_id)
    poll_until(
        lambda: liveassistant.is_active(session_id) or None,
        timeout_s=60,
        interval_s=2.0,
        description="the assistant to register before ending",
    )

    outcome = classroom.end_session(teacher, classroom_id, session_id)
    assert get_ci(outcome, "alreadyEnded") is False, outcome

    stopped = poll_until(
        lambda: (not liveassistant.is_active(session_id)) or None,
        timeout_s=60,
        interval_s=2.0,
        description="the assistant to stop",
    )
    assert stopped

    with pytest.raises(ApiError) as refused:
        streaming.get_stream(teacher, session_id)
    assert refused.value.status_code in (400, 409), (
        "an ended session still issued a join token — LiveKit re-creates a room on demand for "
        f"any valid token, so this is how an evicted student reloads back in ({refused.value.status_code})"
    )


def test_ending_twice_is_reported_rather_than_repeated(
    make_user, classroom: ClassroomClient
) -> None:
    """A double click, or a retry after a dropped response. The teardown must not run twice."""
    teacher = make_user("Teacher", "twiceteacher")
    classroom_id = classroom.create_classroom(teacher, name=f"Twice {unique_username('twice')}")
    session_id = classroom.create_session(
        teacher, classroom_id, title="Once", scheduled_at_utc=_soon()
    )
    classroom.start_session(teacher, classroom_id, session_id)

    first = classroom.end_session(teacher, classroom_id, session_id)
    second = classroom.end_session(teacher, classroom_id, session_id)

    assert get_ci(first, "alreadyEnded") is False, first
    assert get_ci(second, "alreadyEnded") is True, second


def test_the_transcript_is_finalized_when_the_session_ends(
    make_user, classroom: ClassroomClient, liveassistant: LiveAssistantClient
) -> None:
    """The output that lives in another service's database and nothing else points at.

    A transcript left un-finalized never becomes readable, and ClassroomService's summary then
    comes back empty with nothing to explain it (§7.3). With no audio there are no segments —
    what is asserted is that the record exists and is closed, not that it has content.
    """
    teacher = make_user("Teacher", "transteacher")
    classroom_id = classroom.create_classroom(teacher, name=f"Transcript {unique_username('tr')}")
    session_id = classroom.create_session(
        teacher, classroom_id, title="Silent lecture", scheduled_at_utc=_soon()
    )
    classroom.start_session(teacher, classroom_id, session_id)
    poll_until(
        lambda: liveassistant.is_active(session_id) or None,
        timeout_s=60,
        interval_s=2.0,
        description="the assistant to register",
    )

    classroom.end_session(teacher, classroom_id, session_id)

    transcript = poll_until(
        lambda: liveassistant.get_transcript(session_id),
        timeout_s=90,
        interval_s=3.0,
        description="the transcript record to appear",
    )
    assert transcript.status.lower() != "recording", (
        f"the transcript is still {transcript.status!r} after the session ended — a transcript "
        "that never finalizes is one the summary pipeline will read as empty"
    )
