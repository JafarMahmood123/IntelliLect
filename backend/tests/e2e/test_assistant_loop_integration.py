"""§8.5 — the assistant loop, with the model and STT out of the picture.

The loop is: transcript → boundary detection → retrieval → evaluation → feedback card. Four
of those five stages need a model, and models are why this row sat parked — Groq blocks the
VPN exit ips, Ollama needs a machine with the weights on it, and either way the result is a
test whose outcome depends on what a language model felt like saying.

So this suite deliberately does **not** exercise the brain. What it exercises is everything
the brain is bolted to, which is the part this repository owns:

- a started session registers an agent, and an ended one deregisters it;
- the transcript record is created, appended to, and **finalized** — the failure §7.3 found,
  where an un-finalized transcript makes ClassroomService's summary come back empty with
  nothing to explain it;
- retrieval answers for the classroom that owns the material and only that one;
- the internal surface enforces its secret, since that surface *is* the authorization here;
- the pipeline's own counters move, which is how the stages report themselves.

`test_teaching_feedback_e2e.py` is where the real loop runs, behind the `media` and
`feedback` markers. This file is what can be trusted to pass or fail for a reason.

Run: `-m "integration and assistant"`, or with everything else via `-m integration`.
"""

from __future__ import annotations

import logging
from datetime import datetime, timedelta, timezone

import httpx
import pytest

from clients.classroom import ClassroomClient
from clients.http import get_ci
from clients.liveassistant import LiveAssistantClient
from clients.rag import RagClient
from config import Config
from support.ids import unique_username
from support.waiting import poll_until

pytestmark = [pytest.mark.integration, pytest.mark.assistant]

logger = logging.getLogger("e2e.assistant")

MATERIAL = (
    "Water boils at one hundred degrees Celsius at standard atmospheric pressure. "
    "The boiling point falls as altitude increases because atmospheric pressure falls. "
    "Latent heat of vaporisation is the energy needed to change state without a temperature rise. "
) * 5


def _soon() -> str:
    return (datetime.now(timezone.utc) + timedelta(minutes=5)).isoformat()


@pytest.fixture(scope="module")
def taught_session(make_user, classroom: ClassroomClient, knowledge: RagClient, config: Config) -> dict:
    """A classroom with indexed material and a live session — the assistant's input state."""
    teacher = make_user("Teacher", "aiteacher")
    classroom_id = classroom.create_classroom(
        teacher, name=f"Physics {unique_username('ai')}", description="§8.5"
    )
    file_id = get_ci(
        classroom.upload_file(
            teacher, classroom_id, file_name="boiling.txt", content=MATERIAL.encode()
        ),
        "id",
    )
    poll_until(
        lambda: knowledge.document_status(file_id) in ("Indexed", "Failed") or None,
        timeout_s=config.ingest_timeout_s,
        interval_s=2.0,
        description="material to index before the session starts",
    )

    session_id = classroom.create_session(
        teacher, classroom_id, title="Boiling points", scheduled_at_utc=_soon()
    )
    classroom.start_session(teacher, classroom_id, session_id)

    return {
        "teacher": teacher,
        "classroom_id": classroom_id,
        "session_id": session_id,
        "file_id": file_id,
    }


# --- the agent's lifecycle ---------------------------------------------------------------


def test_starting_a_session_registers_an_agent(
    liveassistant: LiveAssistantClient, taught_session: dict
) -> None:
    session_id = taught_session["session_id"]

    active = poll_until(
        lambda: liveassistant.is_active(session_id) or None,
        timeout_s=90,
        interval_s=2.0,
        description="the agent to register the session",
    )
    assert active


def test_the_transcript_record_exists_while_the_session_runs(
    liveassistant: LiveAssistantClient, taught_session: dict
) -> None:
    """The record, not its contents. With no audio there is nothing to transcribe — and that
    is exactly the state in which an un-created record is easiest to miss."""
    transcript = poll_until(
        lambda: liveassistant.get_transcript(taught_session["session_id"]),
        timeout_s=90,
        interval_s=3.0,
        description="the transcript record to be created",
    )

    assert transcript.session_id == taught_session["session_id"]
    assert transcript.classroom_id == taught_session["classroom_id"], (
        "the transcript is filed under a different classroom from the session's — retrieval "
        "during the lecture is scoped by that id, so this would search the wrong material"
    )


# --- retrieval, the one stage that needs no brain ----------------------------------------


def test_retrieval_answers_from_this_classrooms_material(
    knowledge: RagClient, taught_session: dict
) -> None:
    """The stage between the boundary detector and the brain, and the one that decides whether
    the brain has anything to reason about. §7.3 found that when this returns nothing the
    assistant degrades to silence — correct behaviour, and indistinguishable from a broken
    model unless retrieval is checked on its own."""
    results = knowledge.search(taught_session["classroom_id"], "at what temperature does water boil")

    assert results, (
        "retrieval returned nothing for material that is indexed in this classroom. Every "
        "downstream stage degrades to silence from here, so the assistant would look broken "
        "while the brain was fine."
    )
    joined = " ".join(str(get_ci(r, "text", "")) for r in results).lower()
    assert "boil" in joined, joined[:300]


def test_retrieval_is_scoped_to_the_session_classroom(
    make_user, classroom: ClassroomClient, knowledge: RagClient, taught_session: dict, config: Config
) -> None:
    """A second classroom's material must not reach this lecture's assistant.

    The consequence is specific and bad: the brain would compare what the teacher said against
    a different course's notes and raise a discrepancy that is not one.
    """
    other_teacher = make_user("Teacher", "aiother")
    other = classroom.create_classroom(other_teacher, name=f"History {unique_username('aioth')}")
    marker = "The Treaty of Westphalia was signed in sixteen forty eight."
    other_file = get_ci(
        classroom.upload_file(
            other_teacher, other, file_name="history.txt", content=(marker + " ").encode() * 20
        ),
        "id",
    )
    poll_until(
        lambda: knowledge.document_status(other_file) in ("Indexed", "Failed") or None,
        timeout_s=config.ingest_timeout_s,
        interval_s=2.0,
        description="the other classroom's material to index",
    )

    results = knowledge.search(taught_session["classroom_id"], "Treaty of Westphalia", top_k=6)
    joined = " ".join(str(get_ci(r, "text", "")) for r in results).lower()

    assert "westphalia" not in joined, (
        f"another classroom's material reached this lecture's retrieval: {results}"
    )


# --- the surface that IS the authorization -----------------------------------------------


def test_the_assistants_internal_surface_refuses_a_caller_without_the_secret(
    config: Config, taught_session: dict
) -> None:
    """No user token reaches this service; the shared secret is the whole of its access
    control, and §7b found the .NET version of this guard failing OPEN when unconfigured."""
    session_id = taught_session["session_id"]

    for label, headers in {
        "no secret": {},
        "wrong secret": {"X-Internal-Secret": "not-the-secret"},
    }.items():
        response = httpx.get(
            f"{config.liveassistant_url}/api/internal/sessions/{session_id}/transcript",
            headers=headers,
            timeout=15,
        )
        assert response.status_code == 401, (
            f"the transcript route answered {response.status_code} with {label} — that route "
            "returns the full text of what was said in a lecture"
        )


def test_the_correct_secret_is_admitted(config: Config, taught_session: dict) -> None:
    # The case that makes the two refusals mean something: a service that refused everything
    # would pass them both while being entirely unreachable.
    response = httpx.get(
        f"{config.liveassistant_url}/api/internal/sessions/{taught_session['session_id']}/transcript",
        headers=config.internal_headers(),
        timeout=15,
    )

    assert response.status_code != 401, response.text[:200]


# --- the pipeline reports itself ---------------------------------------------------------


def test_the_pipeline_exposes_the_counters_the_latency_harness_reads(
    liveassistant: LiveAssistantClient
) -> None:
    """§9's assistant hop is measured from this service's own histogram rather than from
    outside, because every stage boundary is then on one clock. If the metric is absent the
    latency run reports "unmeasurable" for a reason nothing in its output explains."""
    metrics = liveassistant.metrics()

    assert "suggestions_delivered_total" in metrics or "ideas_detected_total" in metrics, (
        "neither the delivery nor the detection counter is exported; §9's L-3 hop cannot be "
        f"measured. Metrics begin: {metrics[:300]}"
    )


# --- the ending, which is where §7.3's defect lived ---------------------------------------


def test_ending_the_session_finalizes_the_transcript_and_stops_the_agent(
    make_user, classroom: ClassroomClient, liveassistant: LiveAssistantClient
) -> None:
    """§7.3's finding, end to end.

    Every session ends; the well-covered path is the one where the audio simply ran out. The
    cleanup a crash or a cancellation skips is invisible at the time and surfaces later, in
    another service — an un-finalized transcript never becomes readable, so ClassroomService's
    summary comes back empty with nothing to explain it.

    A session of its own, so the module fixture stays live for the tests above.
    """
    teacher = make_user("Teacher", "aiendteacher")
    classroom_id = classroom.create_classroom(teacher, name=f"Ending {unique_username('aiend')}")
    session_id = classroom.create_session(
        teacher, classroom_id, title="Brief", scheduled_at_utc=_soon()
    )
    classroom.start_session(teacher, classroom_id, session_id)
    poll_until(
        lambda: liveassistant.is_active(session_id) or None,
        timeout_s=90,
        interval_s=2.0,
        description="the agent to register",
    )

    classroom.end_session(teacher, classroom_id, session_id)

    stopped = poll_until(
        lambda: (not liveassistant.is_active(session_id)) or None,
        timeout_s=90,
        interval_s=2.0,
        description="the agent to deregister",
    )
    assert stopped

    transcript = poll_until(
        lambda: liveassistant.get_transcript(session_id),
        timeout_s=90,
        interval_s=3.0,
        description="the transcript to be readable after the session ended",
    )
    assert transcript.status.lower() != "recording", (
        f"the transcript is still {transcript.status!r}; the summary pipeline reads a "
        "transcript in that state as empty"
    )
