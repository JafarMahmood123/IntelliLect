"""A deterministic ``BrainClient`` for testing LA-4 without a live model.

Returns a preset ``EvaluationOutcome`` (or raises a configured error) and records
every call, so tests can assert the brain was — or was NOT — invoked (e.g. the
no-results short-circuit must never call it).
"""

from __future__ import annotations

from app.application.ports.brain_client import BrainClient
from app.domain.evaluation.evaluation_outcome import EvaluationOutcome
from app.domain.evaluation.retrieved_chunk import RetrievedChunk
from app.domain.idea.completed_idea import CompletedIdea


class FakeBrainClient(BrainClient):
    def __init__(
        self, outcome: EvaluationOutcome | None = None, *, error: Exception | None = None
    ) -> None:
        self._outcome = outcome or EvaluationOutcome.none()
        self._error = error
        self.calls: list[tuple[CompletedIdea, list[RetrievedChunk]]] = []

    @property
    def called(self) -> bool:
        return bool(self.calls)

    async def evaluate(
        self, idea: CompletedIdea, chunks: list[RetrievedChunk]
    ) -> EvaluationOutcome:
        self.calls.append((idea, list(chunks)))
        if self._error is not None:
            raise self._error
        return self._outcome
