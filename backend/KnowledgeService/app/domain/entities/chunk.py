from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any
from uuid import UUID, uuid4

from app.domain.enums.chunk_source import ChunkSource


def _utcnow() -> datetime:
    return datetime.now(timezone.utc)


@dataclass
class Chunk:
    """A retrievable unit of text derived from a Document.

    The embedding vector deliberately lives on the ORM model, not here — the domain
    should not carry infrastructure concerns like vector dimensionality.
    """

    document_id: UUID
    classroom_id: UUID
    chunk_index: int
    text: str
    token_count: int = 0
    metadata: dict[str, Any] = field(default_factory=dict)
    source: ChunkSource = ChunkSource.TEXT
    id: UUID = field(default_factory=uuid4)
    created_at_utc: datetime = field(default_factory=_utcnow)
