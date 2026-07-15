from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from uuid import UUID

from app.domain.transcript.transcript_status import TranscriptStatus


@dataclass(frozen=True)
class SessionTranscript:
    """The header row for one session's durable transcript (S-0).

    Pure domain object: no framework/ORM imports. Identifies the transcript by its
    ``session_id`` (the natural key — one transcript per session) and records which
    ``classroom_id`` it belongs to plus its lifecycle ``status``. The ordered segment
    bodies live alongside it (see the persisted transcript segments) and are assembled
    on demand for the summary feature.
    """

    session_id: UUID
    classroom_id: UUID
    status: TranscriptStatus
    created_at: datetime
    updated_at: datetime
