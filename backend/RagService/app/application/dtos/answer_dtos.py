from __future__ import annotations

from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field


class AnswerRequest(BaseModel):
    """Inbound answer payload. camelCase aliases match callers; snake_case here."""

    model_config = ConfigDict(populate_by_name=True)

    classroom_id: UUID = Field(alias="classroomId")
    question: str
    # None -> service applies ANSWER_TOP_K.
    top_k: int | None = Field(default=None, alias="topK")


class AnswerSource(BaseModel):
    """One cited chunk that was actually included in the context, with its [n]."""

    model_config = ConfigDict(populate_by_name=True)

    citation: int  # the [n] used in the answer/context
    chunk_id: UUID = Field(alias="chunkId")
    document_id: UUID = Field(alias="documentId")
    page: int | None = None
    slide: int | None = None
    section: str | None = None
    score: float


class AnswerResponse(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    answer: str
    sources: list[AnswerSource]
