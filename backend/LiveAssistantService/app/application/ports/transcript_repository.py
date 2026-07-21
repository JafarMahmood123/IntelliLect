from __future__ import annotations

from abc import ABC, abstractmethod
from uuid import UUID

from app.domain.transcript.session_transcript import SessionTranscript
from app.domain.transcript.stored_segment import StoredSegment
from app.domain.transcript.transcript_segment import TranscriptSegment


class TranscriptRepository(ABC):
    """Durable store for a session's teacher transcript (S-0).

    Final ``TranscriptSegment``s (LA-2) are appended incrementally as they flow from
    the STT, keyed by session, so a mid-session crash never loses the transcript. The
    ordered transcript can be reassembled at any time for the (later) summary feature.

    Application/infrastructure port: implemented over a real DB
    (``SqlAlchemyTranscriptRepository``) or in memory
    (``InMemoryTranscriptRepository``) for offline tests. Implementations must be
    async-safe for a single session's sequential append stream; ``append_segment``
    assigns the next ``order_index`` for the session.
    """

    @abstractmethod
    async def ensure_session(self, session_id: UUID, classroom_id: UUID) -> None:
        """Create the transcript header (status ``Recording``) if it does not exist.

        Idempotent: called at pipeline start; a repeat call for an existing session is
        a no-op and never resets an already-``Finalized`` transcript.
        """
        raise NotImplementedError

    @abstractmethod
    async def append_segment(self, session_id: UUID, segment: TranscriptSegment) -> None:
        """Persist one FINAL segment, assigning it the next sequential ``order_index``.

        Callers must pass only finalized segments (``is_final=True``); interim segments
        are never persisted.
        """
        raise NotImplementedError

    @abstractmethod
    async def get_transcript(self, session_id: UUID) -> list[StoredSegment]:
        """Return the session's stored segments ordered by ``order_index`` (ascending)."""
        raise NotImplementedError

    @abstractmethod
    async def finalize(self, session_id: UUID) -> None:
        """Mark the session's transcript ``Finalized`` (session ended). Idempotent."""
        raise NotImplementedError

    @abstractmethod
    async def assemble_text(self, session_id: UUID) -> str:
        """Return the full transcript: ordered segment texts joined by a single space."""
        raise NotImplementedError

    @abstractmethod
    async def get_session_transcript(self, session_id: UUID) -> SessionTranscript | None:
        """Return the transcript header (classroom/status/timestamps), or None if unknown.

        Read accessor used by the internal transcript endpoint to report classroomId
        and status alongside the assembled text.
        """
        raise NotImplementedError

    @abstractmethod
    async def delete_by_session(self, session_id: UUID) -> bool:
        """Delete a session's transcript (header + all segments). Returns True if a header
        existed and was removed, False if there was nothing to delete.

        Used when the super admin deletes a session and its outputs: idempotent, so a
        re-run of a partially-completed deletion (or a session that never had a transcript)
        is a no-op that returns False rather than an error.
        """
        raise NotImplementedError

    @abstractmethod
    async def delete_by_classroom(self, classroom_id: UUID) -> int:
        """Delete every transcript belonging to a classroom (headers + segments). Returns the
        number of transcript headers removed. Used when a whole classroom is deleted so its
        sessions' transcripts do not outlive it. Idempotent."""
        raise NotImplementedError
