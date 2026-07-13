"""Offline fakes for the answer tests (no Ollama, no DB)."""

from __future__ import annotations

from app.application.dtos.search_dtos import ChunkSearchResult
from app.application.ports.generation_provider import GenerationProvider
from app.application.services.answer_service import AnswerService
from app.application.services.context_builder import ContextBuilder
from app.application.services.retrieval_service import RetrievalService
from app.application.services.token_counter import HeuristicTokenCounter
from app.infrastructure.config.settings import Settings

from tests.retrieval.fakes import FakeChunkRepository, FakeEmbeddingProvider


class FakeGenerationProvider(GenerationProvider):
    """Deterministic generator that records the (system, prompt) it was called with."""

    def __init__(self, answer: str = "Deterministic answer citing [1].") -> None:
        self._answer = answer
        self.calls = 0
        self.last_system: str | None = None
        self.last_prompt: str | None = None

    async def generate(self, system: str, prompt: str) -> str:
        self.calls += 1
        self.last_system = system
        self.last_prompt = prompt
        return self._answer


def build_answer_service(
    results: list[ChunkSearchResult],
    *,
    generator: FakeGenerationProvider | None = None,
    context_max_tokens: int = 6000,
    answer_top_k: int = 6,
) -> tuple[AnswerService, FakeChunkRepository, FakeGenerationProvider]:
    """AnswerService wired to a real RetrievalService + ContextBuilder over fakes."""
    settings = Settings()
    repo = FakeChunkRepository(results)
    retrieval = RetrievalService(
        FakeEmbeddingProvider(settings.embedding_dim),
        repo,
        default_top_k=settings.search_default_top_k,
        max_top_k=settings.search_max_top_k,
    )
    context_builder = ContextBuilder(HeuristicTokenCounter(), context_max_tokens)
    generator = generator or FakeGenerationProvider()
    service = AnswerService(retrieval, context_builder, generator, default_top_k=answer_top_k)
    return service, repo, generator
