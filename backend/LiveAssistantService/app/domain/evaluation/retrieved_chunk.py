from __future__ import annotations

from dataclasses import dataclass
from uuid import UUID


@dataclass(frozen=True)
class RetrievedChunk:
    """One piece of classroom material returned by KnowledgeService retrieval.

    Pure domain object: no library imports. Mirrors a KnowledgeService search result
    item; ``score`` is similarity (higher = more relevant). The location fields are
    mutually exclusive by source format (PDF -> page, PPTX -> slide, DOCX -> section)
    and come from the result's ``metadata``.
    """

    text: str
    score: float
    chunk_id: UUID
    document_id: UUID
    page: int | None = None
    slide: int | None = None
    section: str | None = None
