"""Unit tests for the latency harness's own arithmetic and protocol handling (§9.4).

The harness cannot run without a platform, so it will sit unrun for a while — and when
it finally does run, its output goes into the report as measured fact. That makes its
*own* correctness the risk: a percentile off by one rank, a warm-up sample discarded
from the wrong end, or a SignalR frame carrying two records where only the first is
read, and the report states a number nobody can trace back.

None of that needs a container, so none of it is deferred. These run today.

The platform-readiness gate from conftest is shadowed below: this module talks to
nothing.
"""

from __future__ import annotations

import asyncio
import json

import pytest

from support.latency import Report, Series
from support.signalr import RECORD_SEPARATOR, SignalRClient, to_websocket_url

pytestmark = pytest.mark.latency


@pytest.fixture(scope="session", autouse=True)
def platform_ready() -> None:
    """Shadows conftest's readiness poll — nothing here touches the platform."""
    return None


def _series(samples_ms: list[float], **kwargs) -> Series:
    series = Series(key="X", description="x", budget_p50_ms=100, budget_p95_ms=200, **kwargs)
    series.samples = list(samples_ms)
    return series


# --- percentiles ------------------------------------------------------------------


def test_percentiles_are_observed_values_not_interpolated_ones():
    # 1..20 after the warm-up is discarded => 20 samples. Nearest rank puts p95 at
    # ceil(0.95*20) = the 19th of 20, so 19. Every reported figure is one that was
    # actually measured; an interpolating percentile would answer 19.05, which is not
    # a latency anything experienced.
    series = _series([0.0] + [float(n) for n in range(1, 21)])

    assert series.p95 == 19.0
    assert series.p50 == 10.0
    assert series.worst == 20.0


def test_p95_of_twenty_samples_tolerates_exactly_one_outlier():
    """A property worth pinning rather than discovering during a report write-up.

    At the default sample count, nearest-rank p95 is the second-slowest observation —
    so one catastrophic sample in twenty does not move it. That is what p95 *means*
    (5% are allowed to exceed it), not a bug, but it does mean p95 alone cannot be the
    whole story. It is exactly why the results table carries a `worst` column beside
    it: the outlier is always visible even when it is not what the run is judged on.
    """
    one_bad = _series([0.0] + [50.0] * 19 + [9000.0])
    two_bad = _series([0.0] + [50.0] * 18 + [9000.0, 9000.0])

    assert one_bad.p95 == 50.0
    assert one_bad.worst == 9000.0  # still reported
    assert two_bad.p95 == 9000.0  # a genuine tail does move it


def test_a_single_sample_is_still_reported_rather_than_swallowed():
    # The warm-up rule must not eat the only observation there is. A series that
    # silently became empty would show as "no samples" and pass every budget.
    series = _series([42.0])

    assert series.measured == [42.0]
    assert series.p95 == 42.0


def test_the_warm_up_sample_is_dropped_from_the_front():
    # Dropping from the wrong end discards the slowest observation instead of the
    # coldest one — which is the difference between a conservative measurement and a
    # flattering one.
    series = _series([999.0, 1.0, 2.0, 3.0])

    assert series.measured == [1.0, 2.0, 3.0]
    assert series.worst == 3.0


def test_a_series_that_did_not_come_from_this_harness_keeps_every_sample():
    series = _series([999.0, 1.0, 2.0, 3.0], discard_warmup=False)

    assert series.measured == [999.0, 1.0, 2.0, 3.0]
    assert series.worst == 999.0


def test_samples_are_recorded_in_milliseconds():
    # Everything else in this module sets `samples` directly, so the one conversion in
    # the whole harness would otherwise go unchecked — and a factor of 1000 against
    # budgets written in milliseconds passes or fails every hop for the wrong reason.
    series = Series(key="X", description="x", budget_p50_ms=100, budget_p95_ms=200)
    series.add(0.25)

    assert series.samples == [250.0]


# --- budgets ----------------------------------------------------------------------


def test_a_series_within_budget_reports_no_breach():
    series = _series([500.0] + [50.0] * 20)  # the 500 is the discarded warm-up

    assert series.breaches() == []
    assert "pass" in series.row()


def test_both_percentiles_are_judged_independently():
    # A hop can have a healthy median and an unacceptable tail — which is the normal
    # shape of a latency problem, and the reason a mean would have hidden it.
    series = _series([0.0] + [50.0] * 18 + [900.0, 900.0])

    breaches = series.breaches()

    assert len(breaches) == 1
    assert "p95" in breaches[0]
    assert "FAIL" in series.row()


def test_a_hop_that_could_not_be_measured_never_passes_quietly():
    series = _series([], unavailable="no media reached the subscriber")

    assert series.breaches() == []  # nothing to judge...
    assert "not measured" in series.row()  # ...but the table says so
    assert "no media reached the subscriber" in series.row()

    report = Report()
    report.add(series)
    assert report.unmeasured() == [series]


def test_a_hop_cut_short_still_reports_and_is_still_judged():
    """The case that made the difference between two readings of `unavailable`.

    The audio probe collects sample by sample and can lose the connection partway. If
    "was interrupted" meant "was not measured", those real observations would be
    dropped AND excused from their budget — a hop that was measurably too slow would
    report as merely interrupted. It has samples, so it is judged; it was cut short, so
    the table says INCOMPLETE next to the number.
    """
    series = _series([0.0] + [900.0] * 4, unavailable="connection lost after 4 samples")

    assert series.breaches(), "samples were collected, so the budget applies to them"
    assert "FAIL" in series.row()
    assert "INCOMPLETE" in series.row()
    assert "connection lost after 4 samples" in series.row()

    report = Report()
    report.add(series)
    assert report.unmeasured() == []  # it WAS measured, just not fully
    assert report.incomplete() == [series]


def test_an_empty_series_is_reported_as_empty_rather_than_as_a_pass():
    # The failure mode this guards: a harness bug collects nothing, every budget is
    # trivially met, and the run is green while measuring nothing at all.
    report = Report()
    empty = report.add(_series([]))

    assert report.breaches() == []
    assert report.unmeasured() == [empty]
    assert "not measured" in empty.row()
    assert "harness collected nothing" in empty.row()


def test_the_report_gathers_every_breach_rather_than_stopping_at_the_first():
    report = Report()
    report.add(_series([0.0] + [900.0] * 20)).key = "A"
    report.add(_series([0.0] + [800.0] * 20)).key = "B"

    assert len(report.breaches()) == 4  # p50 and p95 of each


def test_the_table_has_a_row_for_every_series():
    report = Report()
    report.add(_series([0.0, 10.0]))
    report.add(_series([], unavailable="skipped"))

    lines = report.to_markdown().splitlines()

    assert len(lines) == 4  # header, separator, two rows


# --- SignalR protocol -------------------------------------------------------------


def test_url_conversion_keeps_the_scheme_secure_when_it_was():
    assert to_websocket_url("http://localhost", "/hubs/stream") == "ws://localhost/hubs/stream"
    assert to_websocket_url("https://x.test/", "/hubs/stream") == "wss://x.test/hubs/stream"


class FakeWebSocket:
    """Records what was sent; replays what the server would have said."""

    def __init__(self, inbound: list[str]) -> None:
        self.sent: list[str] = []
        self._inbound = asyncio.Queue()
        for frame in inbound:
            self._inbound.put_nowait(frame)

    send_delay_s: float = 0.0

    async def send(self, frame: str) -> None:
        if self.send_delay_s:
            await asyncio.sleep(self.send_delay_s)
        self.sent.append(frame)

    async def recv(self) -> str:
        return await self._inbound.get()

    async def close(self) -> None:
        pass

    def push(self, frame: str) -> None:
        self._inbound.put_nowait(frame)

    def records(self) -> list[dict]:
        return [
            json.loads(record)
            for frame in self.sent
            for record in frame.split(RECORD_SEPARATOR)
            if record
        ]


def _frame(*messages: dict) -> str:
    return "".join(json.dumps(m) + RECORD_SEPARATOR for m in messages)


async def _connected(inbound: list[str]) -> tuple[SignalRClient, FakeWebSocket]:
    socket = FakeWebSocket([_frame({}), *inbound])
    client = SignalRClient("ws://test/hubs/stream", "token", name="test")

    async def fake_connect(*_args, **_kwargs):
        return socket

    import support.signalr as module

    original = module.connect
    module.connect = fake_connect
    try:
        await client.connect()
    finally:
        module.connect = original
    return client, socket


async def test_the_handshake_is_sent_before_anything_else():
    client, socket = await _connected([])
    try:
        assert socket.records()[0] == {"protocol": "json", "version": 1}
    finally:
        await client.close()


async def test_a_refused_handshake_fails_loudly():
    socket = FakeWebSocket([_frame({"error": "Handshake was canceled."})])
    client = SignalRClient("ws://test/hubs/stream", "token")
    import support.signalr as module

    original, module.connect = module.connect, lambda *a, **k: _resolved(socket)
    try:
        with pytest.raises(AssertionError, match="Handshake was canceled"):
            await client.connect()
    finally:
        module.connect = original


async def _resolved(value):
    return value


async def test_several_records_in_one_frame_are_all_dispatched():
    # SignalR packs messages; a reader that json.loads the whole frame sees the first
    # message only, and the harness would report a timeout where the event did arrive.
    client, socket = await _connected([])
    try:
        queue = client.watch("ReceiveChatMessage")
        socket.push(
            _frame(
                {"type": 1, "target": "ReceiveChatMessage", "arguments": ["u", "Sara", "one"]},
                {"type": 1, "target": "ReceiveChatMessage", "arguments": ["u", "Sara", "two"]},
            )
        )

        first = await asyncio.wait_for(queue.get(), 2)
        second = await asyncio.wait_for(queue.get(), 2)

        assert [first.arguments[2], second.arguments[2]] == ["one", "two"]
        # One frame arrived once. Stamping per record instead would charge the second
        # message for the time spent parsing and dispatching the first — small here,
        # and exactly the sort of self-inflicted cost this harness must not report.
        assert first.at == second.at
    finally:
        await client.close()


async def test_a_ping_is_answered_so_a_long_run_is_not_dropped():
    # The server pings every 15s and closes a connection that never replies. A chat
    # series long enough to cross that interval would die mid-measurement and look
    # like a latency failure rather than a protocol one.
    client, socket = await _connected([])
    try:
        socket.push(_frame({"type": 6}))
        await asyncio.sleep(0.05)

        assert {"type": 6} in socket.records()
    finally:
        await client.close()


async def test_events_nobody_is_watching_are_dropped_rather_than_queued():
    # Both ends of every hop live in this process, so each client also receives its own
    # traffic. Queueing it unread would grow without bound across a long run.
    client, socket = await _connected([])
    try:
        client.watch("QuizChanged")
        socket.push(_frame({"type": 1, "target": "ReceiveReaction", "arguments": ["u", "👍"]}))
        await asyncio.sleep(0.05)

        assert client.watch("QuizChanged").empty()
    finally:
        await client.close()


async def test_send_returns_the_stamp_taken_before_the_frame_went_out():
    # The socket is made deliberately slow. Stamping after the write would fold the
    # write itself into the measured interval — invisible against a fast fake, and a
    # silent understatement of every hop against a real one.
    client, socket = await _connected([])
    socket.send_delay_s = 0.2
    try:
        import time

        before = time.perf_counter()
        sent_at = await client.send("SendChatMessage", "session", "hello")
        after = time.perf_counter()

        assert before <= sent_at <= after
        assert sent_at - before < 0.05, "the stamp was taken after the write, not before it"
        assert after - before >= 0.2
        assert socket.records()[-1] == {
            "type": 1,
            "target": "SendChatMessage",
            "arguments": ["session", "hello"],
        }
    finally:
        await client.close()


async def test_invoke_waits_for_the_server_to_confirm():
    # JoinStreamRoom must have completed before the first measurement, or the message
    # is broadcast to a group this client is not yet in.
    client, socket = await _connected([])
    try:
        pending = asyncio.create_task(client.invoke("JoinStreamRoom", "session"))
        await asyncio.sleep(0.05)
        assert not pending.done()

        invocation_id = socket.records()[-1]["invocationId"]
        socket.push(_frame({"type": 3, "invocationId": invocation_id, "result": None}))

        await asyncio.wait_for(pending, 2)
    finally:
        await client.close()


async def test_a_failed_invocation_raises_instead_of_hanging_to_the_timeout():
    client, socket = await _connected([])
    try:
        pending = asyncio.create_task(client.invoke("JoinStreamRoom", "session"))
        await asyncio.sleep(0.05)
        invocation_id = socket.records()[-1]["invocationId"]
        socket.push(_frame({"type": 3, "invocationId": invocation_id, "error": "not authorized"}))

        with pytest.raises(AssertionError, match="not authorized"):
            await asyncio.wait_for(pending, 2)
    finally:
        await client.close()
