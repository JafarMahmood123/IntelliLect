"""In-memory ``TranscriptRepository`` — offline default + test support (S-0).

Holds each session's transcript in a plain dict, assigning ``order_index`` in append
order. It is NOT durable (state dies with the process), so it is used:
  * as the offline default when ``TRANSCRIPT_DB_URL`` is unset (dev without a DB), and
  * by the offline tests to exercise the pipeline wiring and internal endpoint.

Real durability comes from ``SqlAlchemyTranscriptRepository``. An ``asyncio.Lock``
guards the per-session append so a single session's writer assigns a strictly
increasing ``order_index`` even if calls interleave.
"""

from __future__ import annotations

import asyncio
from dataclasses import dataclass, field
from datetime import datetime, timezone
from uuid import UUID

from app.application.ports.transcript_repository import TranscriptRepository
from app.domain.transcript.session_transcript import SessionTranscript
from app.domain.transcript.stored_segment import StoredSegment, assemble_transcript_text
from app.domain.transcript.transcript_segment import TranscriptSegment
from app.domain.transcript.transcript_status import TranscriptStatus


def _now() -> datetime:
    return datetime.now(timezone.utc)


@dataclass
class _Entry:
    header: SessionTranscript
    segments: list[StoredSegment] = field(default_factory=list)


class InMemoryTranscriptRepository(TranscriptRepository):
    def __init__(self) -> None:
        self._sessions: dict[UUID, _Entry] = {}
        self._lock = asyncio.Lock()

    async def ensure_session(self, session_id: UUID, classroom_id: UUID) -> None:
        async with self._lock:
            if session_id in self._sessions:
                return  # idempotent — never resets an existing (possibly finalized) row
            now = _now()
            self._sessions[session_id] = _Entry(
                header=SessionTranscript(
                    session_id=session_id,
                    classroom_id=classroom_id,
                    status=TranscriptStatus.RECORDING,
                    created_at=now,
                    updated_at=now,
                )
            )

    async def append_segment(self, session_id: UUID, segment: TranscriptSegment) -> None:
        async with self._lock:
            entry = self._sessions.get(session_id)
            if entry is None:
                raise KeyError(f"No transcript for session {session_id}; call ensure_session first.")
            entry.segments.append(
                StoredSegment(
                    session_id=session_id,
                    order_index=len(entry.segments),
                    text=segment.text,
                    start_ms=segment.start_ms,
                    end_ms=segment.end_ms,
                    created_at=_now(),
                )
            )
            entry.header = _touch(entry.header)

    async def get_transcript(self, session_id: UUID) -> list[StoredSegment]:
        async with self._lock:
            entry = self._sessions.get(session_id)
            # Already ordered by construction; copy so callers can't mutate our state.
            return list(entry.segments) if entry is not None else []

    async def finalize(self, session_id: UUID) -> None:
        async with self._lock:
            entry = self._sessions.get(session_id)
            if entry is None:
                return
            entry.header = _touch(entry.header, status=TranscriptStatus.FINALIZED)

    async def assemble_text(self, session_id: UUID) -> str:
        return assemble_transcript_text(await self.get_transcript(session_id))

    async def get_session_transcript(self, session_id: UUID) -> SessionTranscript | None:
        async with self._lock:
            entry = self._sessions.get(session_id)
            return entry.header if entry is not None else None

    async def delete_by_session(self, session_id: UUID) -> bool:
        async with self._lock:
            return self._sessions.pop(session_id, None) is not None

    async def delete_by_classroom(self, classroom_id: UUID) -> int:
        async with self._lock:
            doomed = [
                sid
                for sid, entry in self._sessions.items()
                if entry.header.classroom_id == classroom_id
            ]
            for sid in doomed:
                del self._sessions[sid]
            return len(doomed)


def _touch(
    header: SessionTranscript, *, status: TranscriptStatus | None = None
) -> SessionTranscript:
    """Return a copy of ``header`` with ``updated_at`` bumped (and status set if given)."""
    return SessionTranscript(
        session_id=header.session_id,
        classroom_id=header.classroom_id,
        status=status or header.status,
        created_at=header.created_at,
        updated_at=_now(),
    )
