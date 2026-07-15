"""Offline fakes for the session-summary tests (no Ollama, no LiveAssistant, no DB)."""

from __future__ import annotations

from uuid import UUID, uuid4

from app.application.dtos.search_dtos import ChunkSearchResult
from app.application.dtos.summary_dtos import TranscriptDocument
from app.application.ports.generation_provider import GenerationProvider
from app.application.ports.transcript_client import TranscriptClient
from app.application.services.retrieval_service import RetrievalService
from app.application.services.summary_generator import SummaryGenerator
from app.infrastructure.config.settings import Settings

from tests.retrieval.fakes import FakeChunkRepository, FakeEmbeddingProvider

# Deterministic Markdown the fake brain returns for a single-pass / synthesis call.
DETERMINISTIC_MARKDOWN = (
    "# Session Summary\n\n"
    "## Overview\nA deterministic recap of the lecture.\n\n"
    "## Key Points\n- Point one.\n- Point two.\n\n"
    "## Key Terms\n- **Term**: a one-line definition.\n\n"
    "## Notable Moments\n- A clarified misconception.\n"
)


class FakeBrainClient(GenerationProvider):
    """Deterministic generator that RECORDS every (system, prompt) it was called with.

    Records all calls (not just the last) so map-reduce tests can distinguish the
    per-chunk note calls from the final synthesis call.
    """

    def __init__(self, markdown: str = DETERMINISTIC_MARKDOWN) -> None:
        self._markdown = markdown
        self.calls: list[tuple[str, str]] = []

    async def generate(self, system: str, prompt: str) -> str:
        self.calls.append((system, prompt))
        return self._markdown

    # Convenience views for assertions.
    @property
    def call_count(self) -> int:
        return len(self.calls)

    @property
    def prompts(self) -> list[str]:
        return [prompt for _system, prompt in self.calls]

    @property
    def last_prompt(self) -> str | None:
        return self.calls[-1][1] if self.calls else None


class FakeTranscriptClient(TranscriptClient):
    """Returns a scripted transcript and records the session_id it was asked for."""

    def __init__(self, text: str, classroom_id: UUID | None = None) -> None:
        self._text = text
        self._classroom_id = classroom_id or uuid4()
        self.fetched_session_id: UUID | None = None

    @property
    def classroom_id(self) -> UUID:
        return self._classroom_id

    async def fetch(self, session_id: UUID) -> TranscriptDocument:
        self.fetched_session_id = session_id
        return TranscriptDocument(
            session_id=session_id,
            classroom_id=self._classroom_id,
            status="Finalized",
            segment_count=max(1, self._text.count(".")),
            text=self._text,
        )


class RaisingChunkRepository(FakeChunkRepository):
    """A chunk repository whose search() always raises — to test grounding degradation."""

    async def search(self, classroom_id, query_embedding, top_k):
        raise RuntimeError("vector search is down")


def make_chunk(text: str, score: float = 0.9, **metadata) -> ChunkSearchResult:
    return ChunkSearchResult(
        chunk_id=uuid4(),
        document_id=uuid4(),
        text=text,
        score=score,
        chunk_index=0,
        metadata=metadata,
    )


def build_summary_generator(
    *,
    transcript_text: str,
    classroom_id: UUID | None = None,
    chunks: list[ChunkSearchResult] | None = None,
    brain: FakeBrainClient | None = None,
    settings: Settings | None = None,
    raising_retrieval: bool = False,
) -> tuple[SummaryGenerator, FakeTranscriptClient, FakeChunkRepository, FakeBrainClient]:
    """A SummaryGenerator wired to a real RetrievalService over fakes + a fake brain."""
    settings = settings or Settings()
    transcript_client = FakeTranscriptClient(transcript_text, classroom_id)
    repo_cls = RaisingChunkRepository if raising_retrieval else FakeChunkRepository
    repo = repo_cls(chunks or [])
    retrieval = RetrievalService(
        FakeEmbeddingProvider(settings.embedding_dim),
        repo,
        default_top_k=settings.search_default_top_k,
        max_top_k=settings.search_max_top_k,
    )
    brain = brain or FakeBrainClient()
    generator = SummaryGenerator(transcript_client, retrieval, brain, settings)
    return generator, transcript_client, repo, brain
