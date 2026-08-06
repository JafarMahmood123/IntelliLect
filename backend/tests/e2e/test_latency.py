"""Session broadcast latency (work-plan §9). Definitions and budgets: `docs/latency.md`.

Run:

    cd backend && docker compose up -d
    cd tests/e2e && ./run-in-network.sh -m latency

It writes `latency-results.md` next to this file — the table §10.5's report template
consumes, filled in with real numbers rather than transcribed by hand.

**Every hop is measured on one clock.** Not because it is convenient, but because the
alternative is wrong: subtracting a container's timestamp from the host's measures
clock skew as much as latency, and on a laptop that has slept, skew is easily larger
than the thing being measured. So the harness plays both ends of every hop — it sends
as the teacher and receives as the student, in one process — and the only server-side
numbers used (the assistant's) come from a histogram the service fills in-process.

Consequently these are **user-perceived, not server-side**, numbers. That is the right
choice for a budget: nobody experiences the hub's fan-out time. The server-side split
lives in `signalr_broadcast_duration_seconds` (StreamingService) and is what tells you
*where* a missed budget went; see `BroadcastMetricsTests`.

Two hops are not glass-to-glass and say so loudly:
  - L-4 measures SFU transit, excluding the browser capture and playout buffers that
    usually dominate. See `support/livekit_probe.py`.
  - L-3 is the assistant's own in-process histogram, cumulative since the service
    started unless this run drove it.
"""

from __future__ import annotations

import asyncio
import logging
import os
import time
from collections.abc import Iterator
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest

from clients.classroom import ClassroomClient
from clients.liveassistant import LiveAssistantClient
from clients.streaming import StreamingClient
from clients.ums import Account
from config import Config
from support.latency import Report, Series
from support.signalr import SignalRClient, to_websocket_url

logger = logging.getLogger("e2e.latency")

pytestmark = pytest.mark.latency

RESULTS_PATH = Path(__file__).with_name("latency-results.md")

# Sample counts are deliberately small. These are latency budgets, not a load test —
# §10.2 is where concurrency belongs. What matters here is that a slow p95 is a real
# tail and not one cold start, which the discarded warm-up sample already handles.
CHAT_SAMPLES = int(os.environ.get("E2E_LATENCY_CHAT_SAMPLES", "21"))
QUIZ_SAMPLES = int(os.environ.get("E2E_LATENCY_QUIZ_SAMPLES", "9"))
AUDIO_SAMPLES = int(os.environ.get("E2E_LATENCY_AUDIO_SAMPLES", "6"))

HUB_PATH = "/hubs/stream"
RECEIVE_TIMEOUT_S = 20.0


# --- budgets (§9.3; the reasoning for each number is in docs/latency.md) -----------


def _series() -> dict[str, Series]:
    return {
        "L-1": Series(
            key="L-1",
            description="chat send -> other participant renders",
            budget_p50_ms=150,
            budget_p95_ms=350,
        ),
        "L-2a": Series(
            key="L-2a",
            description="quiz publish -> student's socket learns of it",
            budget_p50_ms=300,
            budget_p95_ms=700,
        ),
        "L-2b": Series(
            key="L-2b",
            description="quiz publish -> student holds the quiz (signal + fetch)",
            budget_p50_ms=600,
            budget_p95_ms=1200,
        ),
        "L-3": Series(
            key="L-3",
            description="assistant idea closes -> feedback delivered",
            budget_p50_ms=5000,
            budget_p95_ms=15000,
            discard_warmup=False,
        ),
        "L-4": Series(
            key="L-4",
            description="audio publish -> subscriber (SFU transit ONLY, not glass-to-glass)",
            budget_p50_ms=80,
            budget_p95_ms=150,
        ),
    }


# --- fixtures ---------------------------------------------------------------------


@dataclass
class LiveSession:
    teacher: Account
    student: Account
    classroom_id: str
    session_id: str


@pytest.fixture(scope="module")
def report() -> Iterator[Report]:
    """Collected across the module, judged once, written out whatever happens.

    Written in a finalizer rather than by the judging test: a run that dies halfway
    should still leave the samples it did take. A latency investigation with no table
    because the last hop threw is the worst of both outcomes.
    """
    built = Report()
    yield built
    RESULTS_PATH.write_text(
        "# Session broadcast latency — measured\n\n"
        f"Run at {datetime.now(UTC).isoformat(timespec='seconds')}. "
        "Budgets and their derivations: `docs/latency.md`.\n\n"
        + built.to_markdown()
        + "\n\nEvery figure is client-observed on a single clock. L-4 is SFU transit, NOT\n"
        "glass-to-glass — it excludes the browser capture and playout buffers, which are\n"
        "typically the larger terms. L-3 is the service's own in-process histogram.\n",
        encoding="utf-8",
    )
    logger.info("Wrote %s", RESULTS_PATH)


@pytest.fixture(scope="module")
def series(report: Report) -> dict[str, Series]:
    return {key: report.add(value) for key, value in _series().items()}


@pytest.fixture(scope="module")
def live_session(make_user, classroom: ClassroomClient) -> LiveSession:
    teacher = make_user("Teacher", "lat-teacher")
    student = make_user("Student", "lat-student")
    classroom_id = classroom.create_classroom(teacher, name="Latency harness", description="§9")
    classroom.enroll(student, classroom_id)
    session_id = classroom.create_session(
        teacher,
        classroom_id,
        title="Latency harness session",
        scheduled_at_utc=(datetime.now(UTC) + timedelta(minutes=1)).isoformat(),
    )
    # The stream must be Live: InteractionService rejects chat on a session that is not,
    # so a harness that skipped this would measure an exception path and call it fast.
    classroom.start_session(teacher, classroom_id, session_id)
    return LiveSession(teacher, student, classroom_id, session_id)


async def _joined_hub(config: Config, account: Account, session_id: str, name: str) -> SignalRClient:
    client = SignalRClient(
        to_websocket_url(config.streaming_url, HUB_PATH), account.access_token or "", name=name
    )
    await client.connect()
    # `invoke`, not `send`: group membership must exist before anything is measured, or
    # the first samples would time a message delivered to nobody.
    await client.invoke("JoinStreamRoom", session_id)
    return client


# --- L-1: chat -------------------------------------------------------------------


async def test_chat_send_to_render(
    config: Config, live_session: LiveSession, series: dict[str, Series]
) -> None:
    teacher = await _joined_hub(config, live_session.teacher, live_session.session_id, "teacher")
    try:
        student = await _joined_hub(config, live_session.student, live_session.session_id, "student")
    except Exception:
        await teacher.close()
        raise
    try:
        # Registered before the first send. A queue attached afterwards has already
        # missed the message and the test would time out rather than report a number.
        student.watch("ReceiveChatMessage")

        for index in range(CHAT_SAMPLES):
            sent_at = await teacher.send("SendChatMessage", live_session.session_id, f"probe {index}")
            received = await student.next("ReceiveChatMessage", RECEIVE_TIMEOUT_S)
            series["L-1"].add(received.at - sent_at)
            # Space the samples out. Back-to-back sends pipeline behind one another in
            # the fan-out, so every sample after the first would be measuring queueing
            # this harness caused — a load test's number reported as an idle-path one.
            await asyncio.sleep(0.05)
    finally:
        await teacher.close()
        await student.close()

    assert series["L-1"].measured, "no chat samples collected"
    logger.info("L-1 p50=%.0fms p95=%.0fms", series["L-1"].p50, series["L-1"].p95)


# --- L-2: quiz publish -----------------------------------------------------------


async def test_quiz_publish_to_appear(
    config: Config, classroom: ClassroomClient, live_session: LiveSession, series: dict[str, Series]
) -> None:
    student = await _joined_hub(config, live_session.student, live_session.session_id, "student")
    try:
        student.watch("QuizChanged")

        for index in range(QUIZ_SAMPLES):
            quiz_id = classroom.create_quiz_draft(
                live_session.teacher,
                live_session.classroom_id,
                live_session.session_id,
                title=f"Latency probe {index}",
            )

            def publish() -> tuple[float, object]:
                # Stamped inside the worker thread, immediately before the request goes
                # out. QuizService broadcasts BEFORE returning its response, so timing
                # from when the POST *returns* would routinely produce a negative number.
                started_at = time.perf_counter()
                response = classroom.publish_quiz_response(
                    live_session.teacher, live_session.classroom_id, quiz_id
                )
                return started_at, response

            # httpx here is synchronous; run it off the loop so the student's receive
            # pump keeps draining while the publish is in flight.
            published_at, response = await asyncio.to_thread(publish)
            assert response.is_success, f"publish failed: {response.status_code} {response.text}"

            received = await student.next("QuizChanged", RECEIVE_TIMEOUT_S)
            series["L-2a"].add(received.at - published_at)

            # What the student actually has is the quiz, not the id — the broadcast
            # carries the id only, by design, so the answer key can never travel over
            # the socket. The fetch is therefore part of the hop, not overhead.
            await asyncio.to_thread(
                classroom.get_student_quiz,
                live_session.student,
                live_session.classroom_id,
                quiz_id,
            )
            series["L-2b"].add(time.perf_counter() - published_at)

            await asyncio.to_thread(
                classroom.cancel_quiz, live_session.teacher, live_session.classroom_id, quiz_id
            )
    finally:
        await student.close()

    assert series["L-2a"].measured, "no quiz samples collected"
    logger.info("L-2a p50=%.0fms p95=%.0fms", series["L-2a"].p50, series["L-2a"].p95)
    logger.info("L-2b p50=%.0fms p95=%.0fms", series["L-2b"].p50, series["L-2b"].p95)


# --- L-3: the assistant ----------------------------------------------------------


def test_assistant_idea_to_feedback(
    liveassistant: LiveAssistantClient, series: dict[str, Series]
) -> None:
    """Read the pipeline's own histogram rather than timing it from outside.

    Timing this hop externally is not merely harder, it is not the same measurement:
    "idea closes" is an internal event with no observable moment, so an outside clock
    can only start at end-of-speech, which folds STT windowing into the number and
    makes it incomparable with the 3.10s/12.18s/2.02s baseline already recorded.
    """
    snapshot = liveassistant.histogram("idea_to_feedback_latency_seconds")
    if not snapshot.count:
        series["L-3"].unavailable = (
            "no ideas have completed on this service instance — run "
            "`./run-in-network.sh -m feedback` first, then re-run this"
        )
        pytest.skip(series["L-3"].unavailable)

    # Cumulative since the process started. Reconstruct a sample set from the buckets so
    # the shared percentile/table code applies; each observation is charged at its
    # bucket's upper bound, so the reported figure is an over-estimate. Deliberately:
    # a latency number that errs is far safer erring high.
    previous = 0.0
    for edge, cumulative in snapshot.buckets:
        additional = int(cumulative - previous)
        previous = cumulative
        if edge == float("inf"):
            # Anything past the last finite bucket is charged at the budget's own
            # ceiling. It cannot be attributed a real value, and dropping it would make
            # the slowest observations improve the result.
            edge = series["L-3"].budget_p95_ms / 1000.0
        for _ in range(additional):
            series["L-3"].add(edge)

    logger.info(
        "L-3 over %d observations: mean=%.2fs bucket-p95<=%.2fs",
        snapshot.count,
        snapshot.mean_seconds,
        snapshot.quantile_seconds(0.95),
    )


# --- L-4: SFU transit ------------------------------------------------------------


async def test_audio_publish_to_subscribe(
    config: Config,
    streaming: StreamingClient,
    live_session: LiveSession,
    series: dict[str, Series],
) -> None:
    probe = pytest.importorskip("support.livekit_probe")

    teacher_token = streaming.get_stream(live_session.teacher, live_session.session_id)
    student_token = streaming.get_stream(live_session.student, live_session.session_id)
    # Prefer the host the service itself hands clients — that is the one a browser
    # would dial, and the one the loopback/node-ip constraint applies to.
    ws_url = teacher_token.livekit_host or config.livekit_ws_url

    try:
        async with probe.ToneSubscriber(ws_url, student_token.join_token) as subscriber, \
                probe.TonePublisher(ws_url, teacher_token.join_token) as publisher:
            for _ in range(AUDIO_SAMPLES):
                subscriber.drain()
                emitted_at = await publisher.emit_marker()
                arrived_at = await subscriber.wait_for_marker(RECEIVE_TIMEOUT_S)
                series["L-4"].add(arrived_at - emitted_at)
    except (asyncio.TimeoutError, ConnectionError, OSError) as exc:
        # Media, unlike everything else here, needs UDP to actually flow. It is the one
        # hop a VPN or Docker Desktop can break without breaking anything else, so it
        # reports as not-measured with the reason instead of failing the run.
        series["L-4"].unavailable = f"no media reached the subscriber ({type(exc).__name__}: {exc})"
        pytest.skip(series["L-4"].unavailable)

    assert series["L-4"].measured, "no audio samples collected"
    logger.info("L-4 p50=%.0fms p95=%.0fms", series["L-4"].p50, series["L-4"].p95)


# --- the judgement ---------------------------------------------------------------


def test_every_measured_hop_is_within_budget(report: Report) -> None:
    """One verdict over the whole table.

    Asserting inside each measurement would abort on the first breach, and the first
    breach is exactly the sample you want to read next to the others — a chat p95 of
    900ms means one thing on its own and something else entirely when the quiz hop is
    slow too. So each test collects and this one judges.
    """
    if not report.series:
        pytest.skip("no series collected — run the whole module, not a single test")

    table = report.to_markdown()
    print("\n" + table)

    # Reported, never silently treated as a pass. A green run that measured two of five
    # hops is not a green run, and the table has to say which two.
    unmeasured = report.unmeasured()
    if unmeasured:
        logger.warning(
            "Not measured: %s",
            "; ".join(f"{s.key} ({s.unavailable or 'no samples'})" for s in unmeasured),
        )
    incomplete = report.incomplete()
    if incomplete:
        logger.warning(
            "Measured but cut short (judged anyway, on fewer samples): %s",
            "; ".join(f"{s.key} ({s.unavailable})" for s in incomplete),
        )

    breaches = report.breaches()
    assert not breaches, "Latency budgets missed:\n  " + "\n  ".join(breaches) + "\n\n" + table
