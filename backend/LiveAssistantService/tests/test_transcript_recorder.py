"""TranscriptRecorder (S-0): incremental ordering, interim exclusion, non-fatal, batch.

All offline: an InMemoryTranscriptRepository (or a throwing stub) stands in for the
durable store. No models, no DB, no live session.
"""

from __future__ import annotations

import logging
from uuid import uuid4

from app.application.services.transcript_recorder import TranscriptRecorder
from app.domain.transcript.transcript_segment import TranscriptSegment
from app.domain.transcript.transcript_status import TranscriptStatus
from app.infrastructure.persistence.in_memory_transcript_repository import (
    InMemoryTranscriptRepository,
)


def _final(text: str, start_ms: int, end_ms: int) -> TranscriptSegment:
    return TranscriptSegment(text, start_ms, end_ms, is_final=True, followed_by_pause=False)


def _interim(text: str, start_ms: int, end_ms: int) -> TranscriptSegment:
    return TranscriptSegment(text, start_ms, end_ms, is_final=False, followed_by_pause=False)


async def test_appends_in_order_with_sequential_index_and_assembles_text():
    repo = InMemoryTranscriptRepository()
    sid, cid = uuid4(), uuid4()
    recorder = TranscriptRecorder(repo, sid, cid)

    await recorder.start()
    for i, text in enumerate(["one", "two", "three", "four"]):
        recorder.record(_final(text, i * 1000, (i + 1) * 1000))
    await recorder.finalize()

    stored = await repo.get_transcript(sid)
    assert [s.order_index for s in stored] == [0, 1, 2, 3]
    assert [s.text for s in stored] == ["one", "two", "three", "four"]
    assert await repo.assemble_text(sid) == "one two three four"


async def test_interim_segments_are_not_persisted():
    repo = InMemoryTranscriptRepository()
    sid, cid = uuid4(), uuid4()
    recorder = TranscriptRecorder(repo, sid, cid)

    await recorder.start()
    recorder.record(_interim("unstable partial", 0, 500))   # dropped
    recorder.record(_final("stable text", 0, 1000))
    recorder.record(_interim("another partial", 1000, 1500))  # dropped
    await recorder.finalize()

    stored = await repo.get_transcript(sid)
    assert [s.text for s in stored] == ["stable text"]


async def test_finalize_marks_transcript_finalized():
    repo = InMemoryTranscriptRepository()
    sid, cid = uuid4(), uuid4()
    recorder = TranscriptRecorder(repo, sid, cid)

    await recorder.start()
    header = await repo.get_session_transcript(sid)
    assert header is not None and header.status is TranscriptStatus.RECORDING

    await recorder.finalize()
    header = await repo.get_session_transcript(sid)
    assert header is not None and header.status is TranscriptStatus.FINALIZED


class _FailingAppendRepository(InMemoryTranscriptRepository):
    """Ensures/finalizes normally but every append raises (simulates a DB hiccup)."""

    async def append_segment(self, session_id, segment) -> None:
        raise RuntimeError("append blew up")


async def test_append_failure_is_non_fatal_and_logged(caplog):
    repo = _FailingAppendRepository()
    sid, cid = uuid4(), uuid4()
    recorder = TranscriptRecorder(repo, sid, cid)

    with caplog.at_level(logging.WARNING, logger="liveassistant.transcript"):
        await recorder.start()
        recorder.record(_final("lost segment", 0, 1000))  # never raises on the hot path
        await recorder.finalize()  # drains + finalizes without raising

    assert any("transcript_append_failed" in r.message for r in caplog.records)
    # Nothing persisted, but the session lifecycle still finalized cleanly.
    assert await repo.get_transcript(sid) == []
    header = await repo.get_session_transcript(sid)
    assert header is not None and header.status is TranscriptStatus.FINALIZED


async def test_batch_flush_persists_all_segments_in_order():
    repo = InMemoryTranscriptRepository()
    sid, cid = uuid4(), uuid4()
    recorder = TranscriptRecorder(repo, sid, cid, batch=3)  # flush every 3, tail on finalize

    await recorder.start()
    for i in range(5):  # 5 = one full batch of 3 + a 2-segment tail flushed on finalize
        recorder.record(_final(f"seg{i}", i * 1000, (i + 1) * 1000))
    await recorder.finalize()

    stored = await repo.get_transcript(sid)
    assert [s.order_index for s in stored] == [0, 1, 2, 3, 4]
    assert [s.text for s in stored] == ["seg0", "seg1", "seg2", "seg3", "seg4"]


async def test_out_of_order_timestamps_still_index_by_arrival_order():
    repo = InMemoryTranscriptRepository()
    sid, cid = uuid4(), uuid4()
    recorder = TranscriptRecorder(repo, sid, cid)

    await recorder.start()
    # Arrival order is a, b, c but the stream timestamps are descending.
    recorder.record(_final("a", 3000, 4000))
    recorder.record(_final("b", 1000, 2000))
    recorder.record(_final("c", 0, 1000))
    await recorder.finalize()

    stored = await repo.get_transcript(sid)
    # order_index follows ARRIVAL (transcript) order, not the timestamps.
    assert [(s.order_index, s.text) for s in stored] == [(0, "a"), (1, "b"), (2, "c")]
