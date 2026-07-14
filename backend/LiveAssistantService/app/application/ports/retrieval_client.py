from __future__ import annotations

from abc import ABC, abstractmethod
from uuid import UUID


class RetrievalClient(ABC):
    """Port for fetching classroom material relevant to a finished teacher "idea".

    STUB — NOT IMPLEMENTED THIS PHASE (later phase: retrieval). The concrete
    implementation will call the existing KnowledgeService RAG search
    (``POST /api/search``, classroom-scoped) over ``KNOWLEDGE_BASE_URL`` using the
    shared ``INTERNAL_API_SECRET``. Every method raises ``NotImplementedError``.
    """

    @abstractmethod
    async def search(
        self, classroom_id: UUID, query: str, top_k: int
    ) -> list[dict]:
        """Return the top-k chunks of classroom material relevant to ``query``.

        Expected later-phase behavior: proxy KnowledgeService's classroom-scoped
        vector search and return the retrieved chunks (text + metadata + score) that
        the brain will check the teacher's idea against. Shape mirrors
        KnowledgeService's search response items.
        """
        raise NotImplementedError
