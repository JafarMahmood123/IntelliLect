from __future__ import annotations

from abc import ABC, abstractmethod
from uuid import UUID

from app.domain.entities.chunk import Chunk
from app.domain.extraction.extraction_result import ExtractionResult


class Chunker(ABC):
    """Port that turns an ExtractionResult into ordered, embeddable Chunk objects.

    Implemented in the infrastructure layer (structural by default; semantic when a
    model is available). Chunks never cross a page/slide/section boundary and carry
    location + source metadata and a token count. Embedding and persistence are a
    later phase — this port only produces domain Chunk objects.
    """

    @abstractmethod
    async def chunk(
        self,
        result: ExtractionResult,
        document_id: UUID,
        classroom_id: UUID,
    ) -> list[Chunk]:
        """Return the document's chunks in reading order (global chunk_index from 0)."""
        raise NotImplementedError
