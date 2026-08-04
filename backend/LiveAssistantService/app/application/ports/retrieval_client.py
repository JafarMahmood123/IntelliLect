from __future__ import annotations

from abc import ABC, abstractmethod
from uuid import UUID

from app.domain.evaluation.retrieved_chunk import RetrievedChunk


class RetrievalClient(ABC):
    """Port for fetching classroom material relevant to a finished teacher "idea".

    Implemented by ``RagRetrievalClient``, which calls the existing
    RagService RAG search (``POST /api/search``, classroom-scoped) over
    ``RAG_BASE_URL`` using the shared ``INTERNAL_API_SECRET``. The idea TEXT is
    sent as the query — RagService owns the vector DB and embeds/searches
    internally; this service never touches another service's database.
    """

    @abstractmethod
    async def retrieve(
        self, classroom_id: UUID, query_text: str, top_k: int
    ) -> list[RetrievedChunk]:
        """Return up to ``top_k`` chunks of classroom material relevant to the idea.

        Best-first by similarity score. Implementations raise a clear, catchable error
        on transport/HTTP failure so the caller can decide how to degrade.
        """
        raise NotImplementedError
