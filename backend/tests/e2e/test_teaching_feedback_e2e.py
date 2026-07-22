"""End-to-end: a teacher teaches students and gets feedback to improve.

The scenario, across every service, is:

    register + approve + login (UserManagementService)
        -> create classroom + enroll students + create session (ClassroomService)
        -> start session  ── triggers ──▶  StreamingService (LiveKit room + egress)
                                              └─ notifies ─▶ LiveAssistantService (agent joins)
        -> teacher speaks into the LiveKit room (synthetic participant, canned audio)
        -> agent: audio → STT → idea → retrieve vs seeded material → LLM eval → pace
        -> feedback data message delivered back to the TEACHER only
        -> transcript persisted; agent session torn down.

Two tests:
  * test_session_orchestration_seams — deterministic; proves the whole cross-service
    wiring up to the agent registering the session. No audio / Ollama / TTS needed.
  * test_teacher_teaches_and_gets_feedback — the real AI loop with a synthetic teacher
    publishing speech and receiving the improvement suggestion. (@pytest.mark.media)
"""

from __future__ import annotations

import logging
import uuid
from datetime import datetime, timezone

import pytest

from clients.classroom import ClassroomClient
from clients.knowledge import KnowledgeClient
from clients.liveassistant import LiveAssistantClient
from clients.streaming import StreamingClient
from config import Config
from support.material import MinioSeeder, make_pdf_bytes
from support.scenario import provision_classroom
from support.tts import TtsUnavailable, synthesize_teacher_wav
from support.waiting import poll_until

logger = logging.getLogger("e2e.test")

# The fact we seed, and the contradiction the teacher will state aloud.
SEEDED_FACT_TITLE = "Physics: Boiling Point of Water"
SEEDED_FACT_PARAGRAPHS = [
    "Water boils at one hundred degrees Celsius at sea level.",
    "This is a fundamental property of water at standard atmospheric pressure.",
    "The boiling point of water at sea level is one hundred degrees Celsius.",
]
TEACHER_WRONG_LINE = (
    "Today we will learn about water. "
    "Water boils at fifty degrees Celsius at sea level. "
    "Remember, fifty degrees Celsius is the boiling point of water."
)


def _now_iso() -> str:
    # Trailing 'Z' (not '+00:00') so ASP.NET parses it as DateTimeKind.Utc — Npgsql
    # rejects non-UTC DateTimes for 'timestamp with time zone' columns.
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def _start_session_resiliently(
    classroom: ClassroomClient,
    streaming: StreamingClient,
    teacher,
    classroom_id: str,
    session_id: str,
    *,
    attempts: int = 3,
) -> None:
    """Start the session, tolerating a transient gateway 504 / read timeout.

    On a loaded host the synchronous start chain (Classroom -> Streaming -> egress)
    can exceed nginx's 60s proxy timeout even though the DB transaction rolls back.
    After such a failure we check whether the stream actually came up; if not, and the
    session is still Scheduled (atomic UoW rolled back), we retry.
    """
    last_error: Exception | None = None
    for attempt in range(attempts):
        try:
            classroom.start_session(teacher, classroom_id, session_id)
            return
        except Exception as exc:  # noqa: BLE001 — 504/timeout under load
            last_error = exc
            logger.warning("start_session attempt %d failed: %s", attempt + 1, exc)
            # Did it actually go Live despite the lost response?
            try:
                if streaming.get_stream(teacher, session_id).status.lower() == "live":
                    logger.info("stream is Live despite the error; continuing.")
                    return
            except Exception:  # noqa: BLE001 — no stream yet -> the UoW rolled back
                pass
    raise AssertionError(f"could not start session after {attempts} attempts: {last_error}")


def _livekit_ws(token_host: str, config: Config) -> str:
    # Prefer the configured URL (E2E_LIVEKIT_WS_URL) — on a host run it equals the
    # token's host (the LAN IP); when running INSIDE the docker network it is set to
    # ws://livekit-server:7880 so media flows container<->container (the token stays
    # valid — same LiveKit server, just a different address). Fall back to the token
    # host only if the config was explicitly blanked.
    if config.livekit_ws_url:
        return config.livekit_ws_url
    return token_host


# ---------------------------------------------------------------------------
# Test 1 — deterministic cross-service orchestration (no media, no Ollama).
# ---------------------------------------------------------------------------
def test_session_orchestration_seams(
    make_user,
    classroom: ClassroomClient,
    streaming: StreamingClient,
    liveassistant: LiveAssistantClient,
    config: Config,
) -> None:
    ctx = provision_classroom(
        make_user, classroom, student_count=config.student_count,
        classroom_name="E2E Seams Classroom",
    )

    session_id = classroom.create_session(
        ctx.teacher, ctx.classroom_id,
        title="Seams check", scheduled_at_utc=_now_iso(), participation_mode=1,
    )

    # Starting the session is the single trigger that cascades across services.
    _start_session_resiliently(classroom, streaming, ctx.teacher, ctx.classroom_id, session_id)

    # StreamingService created the LiveKit room and mints a teacher join token.
    teacher_stream = streaming.get_stream(ctx.teacher, session_id)
    assert teacher_stream.status.lower() == "live", teacher_stream
    assert teacher_stream.join_token, "teacher did not receive a LiveKit join token"
    assert teacher_stream.livekit_host, "no LiveKit host returned"

    # Each student can also mint a join token for the same room.
    for student in ctx.students:
        student_stream = streaming.get_stream(student, session_id)
        assert student_stream.join_token
        assert student_stream.session_id == session_id

    # The .NET -> Python seam fired: LiveAssistant registered the agent session.
    poll_until(
        lambda: liveassistant.is_active(session_id),
        timeout_s=30,
        description="LiveAssistant to register the agent session",
    )
    logger.info("Cross-service seam verified: session %s active on the agent.", session_id)

    # Teardown: stopping the agent session finalizes the (empty) transcript.
    liveassistant.stop_session(session_id)
    poll_until(
        lambda: not liveassistant.is_active(session_id),
        timeout_s=30,
        description="agent session to tear down",
    )


# ---------------------------------------------------------------------------
# Test 2 — the real AI feedback loop with a synthetic teacher.
# ---------------------------------------------------------------------------
@pytest.mark.media
async def test_teacher_teaches_and_gets_feedback(
    make_user,
    classroom: ClassroomClient,
    streaming: StreamingClient,
    liveassistant: LiveAssistantClient,
    knowledge: KnowledgeClient,
    config: Config,
) -> None:
    # Synthesize the teacher's spoken line first — if no TTS is available, skip the
    # media loop cleanly (the orchestration test above still covers the wiring).
    try:
        wav_path = synthesize_teacher_wav(
            TEACHER_WRONG_LINE,
            teacher_wav_path=config.teacher_wav_path,
            piper_model_path=config.piper_model_path,
        )
    except TtsUnavailable as exc:
        pytest.skip(str(exc))

    ctx = provision_classroom(
        make_user, classroom, student_count=config.student_count,
        classroom_name="E2E Feedback Classroom",
    )

    # --- Seed the classroom with material the teacher will contradict ---------
    seeder = MinioSeeder(
        config.minio_endpoint, config.minio_access_key, config.minio_secret_key,
        secure=config.minio_secure, bucket=config.s3_bucket,
    )
    file_id = str(uuid.uuid4())
    s3_key = f"e2e/{ctx.classroom_id}/{file_id}.pdf"
    pdf = make_pdf_bytes(SEEDED_FACT_TITLE, SEEDED_FACT_PARAGRAPHS)
    seeder.put(s3_key, pdf, "application/pdf")
    knowledge.ingest(
        file_id=file_id, classroom_id=ctx.classroom_id, s3_key=s3_key,
        file_name="boiling-point.pdf", content_type="application/pdf", size_bytes=len(pdf),
    )
    def _final_status() -> str | None:
        # Return only a terminal status so poll_until keeps waiting on Pending/Processing;
        # a transient ReadTimeout (Ollama busy) is caught by poll_until and retried.
        status = knowledge.document_status(file_id)
        return status if status in ("Done", "Failed") else None

    status = poll_until(
        _final_status,
        timeout_s=config.ingest_timeout_s,
        description="KnowledgeService to index the seeded document",
    )
    assert status == "Done", f"document ingestion ended in status {status!r}"
    # Sanity: the seeded fact is now retrievable for this classroom (retry through
    # transient Ollama-busy timeouts on the query-embedding path).
    results = poll_until(
        lambda: knowledge.search(ctx.classroom_id, "boiling point of water", top_k=6) or None,
        timeout_s=90,
        description="seeded material to become searchable",
    )
    assert results, "seeded material is not searchable — retrieval would short-circuit"

    # --- Start the live session (cascades to Streaming + LiveAssistant) -------
    session_id = classroom.create_session(
        ctx.teacher, ctx.classroom_id,
        title="Water lesson", scheduled_at_utc=_now_iso(), participation_mode=1,
    )
    _start_session_resiliently(classroom, streaming, ctx.teacher, ctx.classroom_id, session_id)
    teacher_stream = streaming.get_stream(ctx.teacher, session_id)
    poll_until(
        lambda: liveassistant.is_active(session_id),
        timeout_s=30,
        description="the agent to join the room",
    )

    ideas_before = liveassistant.counter_total("ideas_detected_total")
    evals_before = liveassistant.counter_total("evaluations_total")

    # --- The teacher speaks; the agent listens and should send feedback -------
    # Import here so the non-media test never requires the LiveKit SDK.
    from livekit.rtc.room import ConnectError

    from support.livekit_teacher import SyntheticTeacher

    ws_url = _livekit_ws(teacher_stream.livekit_host, config)
    try:
        async with SyntheticTeacher(ws_url, teacher_stream.join_token, wav_path) as teacher:
            # Repeat the line so STT+boundary have enough to segment a complete idea.
            await teacher.speak(repeat=3, realtime=True)
            feedback = await teacher.wait_for_feedback(config.feedback_timeout_s)
    except ConnectError as exc:
        # Signaling succeeded but the WebRTC peer connection could not be established
        # ("wait_pc_connection timed out"). This is an environment prerequisite, not a
        # product defect: the synthetic teacher's media can't reach LiveKit. Common
        # causes on this host: a VPN whose firewall drops the inter-container UDP
        # (LiveKit shows srflx candidates via the VPN exit IP), and/or Docker Desktop's
        # VM breaking the host<->container media hairpin for the pinned --node-ip.
        # Run with the VPN off (or allowing the docker subnet), ideally on native
        # Linux docker; see README "Media transport".
        pytest.skip(
            f"LiveKit media transport unavailable ({exc}). The cross-service wiring is "
            "still covered by test_session_orchestration_seams. See README > Media transport."
        )

    # --- Assert the server side of the loop ran -------------------------------
    transcript = poll_until(
        lambda: (t := liveassistant.get_transcript(session_id)) and t.segment_count > 0 and t,
        timeout_s=30,
        description="the transcript to capture the teacher's speech",
    )
    logger.info("Transcript (%d segments): %r", transcript.segment_count, transcript.text)
    assert transcript.segment_count > 0

    ideas_after = liveassistant.counter_total("ideas_detected_total")
    evals_after = liveassistant.counter_total("evaluations_total")
    assert ideas_after > ideas_before, "no idea boundary was detected from the speech"
    assert evals_after > evals_before, "the brain never evaluated an idea against the material"

    # --- The headline: the teacher received an improvement suggestion ---------
    assert feedback is not None, (
        "No teaching_suggestion feedback was delivered to the teacher within "
        f"{config.feedback_timeout_s:.0f}s. Transcript was: {transcript.text!r}"
    )
    assert feedback.get("type") == "teaching_suggestion"
    assert str(feedback.get("session_id")) == str(session_id)
    assert feedback.get("text"), "feedback payload carried no suggestion text"
    logger.info(
        "Teacher received feedback [%s]: %s",
        feedback.get("feedback_type"), feedback.get("text"),
    )

    # --- Teardown -------------------------------------------------------------
    liveassistant.stop_session(session_id)
