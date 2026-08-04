from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field


@dataclass(frozen=True)
class TranscriptDocument:
    """The assembled session transcript fetched from LiveAssistantService (S-0).

    Framework-free value object mirroring the S-0 internal endpoint payload
    (``GET /api/internal/sessions/{id}/transcript``). ``text`` is the ordered,
    space-joined transcript; ``classroom_id`` scopes any grounding retrieval.
    """

    session_id: UUID
    classroom_id: UUID
    status: str
    segment_count: int
    text: str


@dataclass(frozen=True)
class SummaryResult:
    """A generated, structured Markdown summary of one lecture session (S-1).

    Output stops at Markdown here — PDF rendering (S-2) and storage/upload (S-3) are
    later phases. ``model`` records which generative model produced it; ``session_id``
    is None when the summary was generated from raw text (no session fetch).
    """

    session_id: UUID | None
    classroom_id: UUID
    markdown: str
    model: str
    generated_at: datetime


class SummarizeRequest(BaseModel):
    """Body of the session-end summarize trigger (S-3). camelCase from the caller.

    ``classroomId`` is provided by the session-end caller so a summary FAILURE can still
    be reported for the right classroom even if the transcript fetch fails.
    """

    model_config = ConfigDict(populate_by_name=True)

    classroom_id: UUID = Field(alias="classroomId")


class SummarizeResponse(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    session_id: UUID = Field(alias="sessionId")
    status: str  # "accepted" | "skipped" | "in-progress"
