from __future__ import annotations

from abc import ABC, abstractmethod

from app.domain.evaluation.evaluation_outcome import EvaluationOutcome
from app.domain.evaluation.retrieved_chunk import RetrievedChunk
from app.domain.idea.completed_idea import CompletedIdea
from app.domain.quiz.generated_quiz import GeneratedQuiz


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

    async def generate_quiz(
        self,
        idea_text: str,
        chunks: list[RetrievedChunk],
        *,
        question_count: int,
        min_options: int,
        max_options: int,
    ) -> GeneratedQuiz | None:
        """Write multiple-choice questions testing the explanation in ``idea_text``.

        The bounds are the CALLER's (ClassroomService owns the quiz limits), so a provider never
        proposes a quiz the limits would reject. Implementations must hand them to the model as a
        response schema where the provider supports one, so the shape is constrained during
        generation rather than merely checked afterwards.

        Returns ``None`` when the reply yielded no usable question. Unlike ``evaluate``, silence is
        NOT an acceptable outcome here — a teacher asked for this — so the caller reports the
        failure instead of pretending there was nothing to say. Transport/HTTP failures may raise
        a catchable error.
        """
        raise NotImplementedError

    async def smoke_complete(self, transcript_text: str) -> str:
        """SMOKE-TEST ONLY (temporary): send raw transcript text to the model and return its raw
        reply, bypassing the grounded evaluation prompt + JSON contract.

        Concrete, non-abstract on purpose so test fakes need not implement it; the real
        ``OllamaBrainClient`` overrides it. Remove with the smoke branch once verified.
        """
        raise NotImplementedError
