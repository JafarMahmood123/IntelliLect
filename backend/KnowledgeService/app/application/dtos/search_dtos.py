from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field


@dataclass
class ChunkSearchResult:
    """A single vector-search hit returned by ChunkRepository.search (best-first).

    Framework-free value object (no pydantic): the repository/port layer returns
    these, and the API maps them to SearchResultItem for the response.
    """

    chunk_id: UUID
    document_id: UUID
    text: str
    score: float  # higher is more similar (1 - cosine_distance)
    chunk_index: int
    metadata: dict[str, Any] = field(default_factory=dict)


class SearchRequest(BaseModel):
    """Inbound search payload. camelCase aliases match the callers; snake_case here."""

    model_config = ConfigDict(populate_by_name=True)

    classroom_id: UUID = Field(alias="classroomId")
    query: str
    # None -> service applies SEARCH_DEFAULT_TOP_K; clamped to [1, SEARCH_MAX_TOP_K].
    top_k: int | None = Field(default=None, alias="topK")


class SearchResultItem(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    chunk_id: UUID = Field(alias="chunkId")
    document_id: UUID = Field(alias="documentId")
    text: str
    score: float
    chunk_index: int = Field(alias="chunkIndex")
    metadata: dict[str, Any] = Field(default_factory=dict)

    @classmethod
    def from_result(cls, result: ChunkSearchResult) -> SearchResultItem:
        return cls(
            chunk_id=result.chunk_id,
            document_id=result.document_id,
            text=result.text,
            score=result.score,
            chunk_index=result.chunk_index,
            metadata=result.metadata,
        )


class SearchResponse(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    results: list[SearchResultItem]
