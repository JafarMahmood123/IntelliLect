from __future__ import annotations

from abc import ABC, abstractmethod

from app.domain.evaluation.evaluation_outcome import EvaluationOutcome
from app.domain.evaluation.retrieved_chunk import RetrievedChunk
from app.domain.idea.completed_idea import CompletedIdea


class BrainClient(ABC):
    """Port for evaluating a teacher's finished "idea" against retrieved material.

    Implemented by ``OllamaBrainClient``, which calls a local generative model (host
    Ollama) with this service's own grounded, silence-biased evaluation prompt and
    returns a structured outcome — either "no feedback" (the common case) or a single
    suggestion citing the material. It uses ONLY the provided chunks, never outside
    knowledge.
    """

    @abstractmethod
    async def evaluate(
        self, idea: CompletedIdea, chunks: list[RetrievedChunk]
    ) -> EvaluationOutcome:
        """Judge ``idea`` against the numbered ``chunks`` and return an outcome.

        A parse/validation failure of the model output must degrade to "no feedback"
        rather than crash the caller; transport/HTTP failures may raise a catchable
        error for the caller to handle.
        """
        raise NotImplementedError
