from __future__ import annotations

from abc import ABC, abstractmethod
from uuid import UUID


class BrainClient(ABC):
    """Port for evaluating a teacher's finished "idea" against retrieved material.

    STUB — NOT IMPLEMENTED THIS PHASE (later phase: brain evaluation). The concrete
    implementation will call a local generative model (reusing KnowledgeService's
    answering/RAG capability, or a dedicated evaluation prompt) to decide whether the
    idea is consistent with the classroom's uploaded material and, if not, what the
    private correction should be. Every method raises ``NotImplementedError``.
    """

    @abstractmethod
    async def evaluate(
        self, classroom_id: UUID, idea: str, context: list[dict]
    ) -> dict:
        """Judge ``idea`` against ``context`` (retrieved chunks) for the classroom.

        Expected later-phase behavior: return a verdict indicating whether the idea
        is supported/contradicted by the material and, when a correction is warranted,
        the suggested private note for the teacher (with citations). The exact return
        shape is defined when this phase is built.
        """
        raise NotImplementedError
