from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from uuid import UUID


@dataclass(frozen=True)
class StoredSegment:
    """A FINAL transcript segment as persisted for a session (S-0).

    The durable counterpart of the live ``TranscriptSegment`` (LA-2): only finalized
    segments are stored, and each is stamped with a sequential ``order_index`` assigned
    at append time so the transcript can be reconstructed in the exact order the
    teacher spoke — independent of the segments' stream timestamps. ``start_ms`` /
    ``end_ms`` are carried over from the live segment for later alignment.
    """

    session_id: UUID
    order_index: int
    text: str
    start_ms: int
    end_ms: int
    created_at: datetime


def assemble_transcript_text(segments: list[StoredSegment]) -> str:
    """Join ordered segment texts into the full transcript.

    Single canonical rule shared by every ``TranscriptRepository`` so the DB and
    in-memory stores assemble identically: strip each segment, drop empties, join with
    a single space. ``segments`` must already be ordered by ``order_index``.
    """
    return " ".join(segment.text.strip() for segment in segments if segment.text.strip())
