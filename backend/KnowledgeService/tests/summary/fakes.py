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


class RecordingEmbeddingProvider(FakeEmbeddingProvider):
    """FakeEmbeddingProvider that keeps EVERY query, not just the last one.

    Grounding issues one query per transcript window, so a test can only prove the
    windows span the transcript by seeing all of them.
    """

    def __init__(self, dim: int) -> None:
        super().__init__(dim)
        self.queries: list[str] = []

    async def embed_query(self, text: str) -> list[float]:
        self.queries.append(text)
        return await super().embed_query(text)


class WindowedChunkRepository(FakeChunkRepository):
    """Serves a DIFFERENT result set per search call, in call order.

    Mirrors the real thing: each transcript window retrieves material about a different
    part of the lecture, which is the entire reason for sampling more than one. An entry
    that is an exception instance is raised instead of returned, so a partial-failure
    test needs no separate double.
    """

    def __init__(
        self,
        per_call: list[list[ChunkSearchResult] | BaseException],
        fallback: list[ChunkSearchResult] | None = None,
    ) -> None:
        super().__init__(fallback or [])
        self._per_call = list(per_call)
        self.search_count = 0
        self.searched_top_ks: list[int] = []

    async def search(self, classroom_id, query_embedding, top_k):
        index = self.search_count
        self.search_count += 1
        self.searched_classroom_id = classroom_id
        self.searched_top_ks.append(top_k)
        outcome = self._per_call[index] if index < len(self._per_call) else self._results
        if isinstance(outcome, BaseException):
            raise outcome
        return outcome[:top_k]


def build_grounding_generator(
    *,
    transcript_text: str,
    per_window_chunks: list[list[ChunkSearchResult] | BaseException],
    settings: Settings | None = None,
) -> tuple[SummaryGenerator, RecordingEmbeddingProvider, WindowedChunkRepository, FakeBrainClient]:
    """A generator whose retrieval answers each grounding window differently.

    Separate from `build_summary_generator` because these tests assert on the QUERIES
    themselves, which needs the recording embedder in hand.
    """
    settings = settings or Settings()
    embedder = RecordingEmbeddingProvider(settings.embedding_dim)
    repo = WindowedChunkRepository(per_window_chunks)
    retrieval = RetrievalService(
        embedder,
        repo,
        default_top_k=settings.search_default_top_k,
        max_top_k=settings.search_max_top_k,
    )
    brain = FakeBrainClient()
    generator = SummaryGenerator(
        FakeTranscriptClient(transcript_text), retrieval, brain, settings
    )
    return generator, embedder, repo, brain


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
