"""The single-flight runner around the sweep.

This is deliberately not a queue: a re-embed is one whole-corpus sweep, and two of them race for
the same NULL rows. Both would fetch the same batch, both would embed it, and both would pay for
it — the database ends up correct and the bill does not, which is why the duplication is easy to
miss.

The other half is that a failed sweep must leave a readable reason behind. There is no
foreground caller to raise into: the operator POSTs, gets a 202, and everything after that is
only visible through the status endpoint.
"""

from __future__ import annotations

import asyncio

from app.application.services.reembed_runner import ReembedRunner
from app.application.services.reembed_service import ReembedProgress


def _blocking_sweep(release: asyncio.Event):
    async def sweep(progress: ReembedProgress) -> None:
        progress.total = 1
        await release.wait()

    return sweep


async def _settle(runner: ReembedRunner) -> None:
    """Yield until the runner reports itself finished.

    Bounded rather than a fixed number of `sleep(0)` calls: the number of scheduler turns a
    sweep needs is an implementation detail, and a test that happens to yield exactly enough
    times today passes for a reason that can change.
    """
    for _ in range(100):
        if not runner.is_running():
            return
        await asyncio.sleep(0)
    raise AssertionError("the sweep never finished")


async def test_a_sweep_runs_and_reports_completion():
    async def sweep(progress: ReembedProgress) -> None:
        progress.embedded = 7

    runner = ReembedRunner(sweep)

    assert runner.start() is True
    await _settle(runner)

    assert runner.progress().state == "completed"
    assert runner.progress().embedded == 7
    assert runner.is_running() is False


async def test_a_second_start_is_refused_rather_than_queued():
    # The duplicate-work guard. Queueing would be worse than refusing: the second run starts
    # against a corpus the first is still changing, and re-embeds whatever it has not reached.
    release = asyncio.Event()
    runner = ReembedRunner(_blocking_sweep(release))

    assert runner.start() is True
    await asyncio.sleep(0)
    assert runner.is_running() is True

    assert runner.start() is False

    release.set()
    await _settle(runner)
    assert runner.progress().state == "completed"


async def test_a_finished_run_does_not_block_the_next_one():
    # The flip side: single-flight must not become single-use. An operator who fixes the key and
    # POSTs again has to be able to start a sweep.
    runs = 0

    async def sweep(_progress: ReembedProgress) -> None:
        nonlocal runs
        runs += 1

    runner = ReembedRunner(sweep)
    assert runner.start() is True
    await _settle(runner)

    assert runner.start() is True
    await _settle(runner)

    assert runs == 2


async def test_a_failure_is_kept_for_the_status_endpoint_to_report():
    """Nobody is waiting on this call, so an exception that only propagates is an exception
    nobody sees. The three realistic causes — a wrong dimension, a rejected key, a rate limit —
    are all operator-fixable, and all of them need the message, not just the type."""

    async def sweep(_progress: ReembedProgress) -> None:
        raise ValueError("the configured embedder returns 1024-dimensional vectors")

    runner = ReembedRunner(sweep)
    runner.start()
    await _settle(runner)

    assert runner.progress().state == "failed"
    assert "ValueError" in runner.progress().error
    assert "1024-dimensional" in runner.progress().error


async def test_progress_written_before_a_failure_survives_it():
    # Half a sweep is still half a sweep: those chunks are committed, and the operator needs to
    # know the run got that far rather than assuming it must start over.
    async def sweep(progress: ReembedProgress) -> None:
        progress.total = 100
        progress.embedded = 40
        raise RuntimeError("rate limited")

    runner = ReembedRunner(sweep)
    runner.start()
    await _settle(runner)

    assert runner.progress().embedded == 40
    assert runner.progress().state == "failed"


async def test_shutdown_cancels_an_in_flight_sweep_and_says_so():
    # Called on app shutdown. A cancelled sweep is not a completed one, and the distinction
    # matters to whoever reads the status after a restart and decides whether to run it again.
    release = asyncio.Event()
    runner = ReembedRunner(_blocking_sweep(release))
    runner.start()
    await asyncio.sleep(0)

    await runner.stop()

    assert runner.is_running() is False
    assert runner.progress().state == "failed"
    assert runner.progress().error == "cancelled"
    # Whatever the sweep had recorded is still there.
    assert runner.progress().total == 1


async def test_shutdown_with_nothing_running_is_harmless():
    # Shutdown runs whether or not anyone ever started a sweep.
    runner = ReembedRunner(lambda _p: asyncio.sleep(0))

    await runner.stop()

    assert runner.is_running() is False
    assert runner.progress().state == "idle"


async def test_starting_resets_the_previous_run_s_error():
    # A stale "failed" left on the snapshot would make a fresh, healthy run look broken on the
    # status endpoint.
    release = asyncio.Event()
    calls = 0

    async def sweep(_progress: ReembedProgress) -> None:
        nonlocal calls
        calls += 1
        if calls == 1:
            raise RuntimeError("bad key")
        await release.wait()

    runner = ReembedRunner(sweep)
    runner.start()
    await _settle(runner)
    assert runner.progress().state == "failed"

    runner.start()
    await asyncio.sleep(0)

    assert runner.progress().state == "running"
    assert runner.progress().error is None

    release.set()
    await _settle(runner)
