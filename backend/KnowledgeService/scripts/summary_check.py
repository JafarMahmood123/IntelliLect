"""Generate a structured Markdown session summary (S-1).

Default (OFFLINE): a scripted lecture transcript + fake retrieval + a fake generator —
no Ollama, no LiveAssistantService, no Postgres. Proves the structure/flow and prints
the Markdown.

    python scripts/summary_check.py
    python scripts/summary_check.py --long        # force the map-reduce path

--live (DEFERRED): fetch the real transcript from LiveAssistantService, ground against
indexed classroom material, and generate with the real qwen2.5:7b-instruct. Requires:
  - LiveAssistantService running with the session's transcript persisted (S-0), and
    LIVE_ASSISTANT_BASE_URL / INTERNAL_API_SECRET set;
  - Ollama running with the summary model pulled (`ollama pull qwen2.5:7b-instruct`);
  - Postgres up with indexed material for the classroom (for grounding).

    python scripts/summary_check.py --live <session-uuid>
"""

from __future__ import annotations

import argparse
import asyncio
import sys
from uuid import UUID, uuid4

from app.application.dtos.search_dtos import ChunkSearchResult, SearchResponse
from app.application.dtos.summary_dtos import TranscriptDocument
from app.application.ports.generation_provider import GenerationProvider
from app.application.ports.transcript_client import TranscriptClient
from app.application.services.summary_generator import SummaryGenerator
from app.infrastructure.config.settings import get_settings

# --- Offline scaffolding ------------------------------------------------------

_LECTURE = (
    "Today we are covering photosynthesis. Photosynthesis is the process by which "
    "plants convert light energy into chemical energy stored as glucose. It happens "
    "mainly in the chloroplasts, which contain the green pigment chlorophyll. There "
    "are two stages: the light-dependent reactions and the Calvin cycle. In the "
    "light-dependent reactions, water is split, oxygen is released, and ATP and NADPH "
    "are produced. In the Calvin cycle, carbon dioxide is fixed into glucose using the "
    "ATP and NADPH from the first stage. Remember that the overall equation takes "
    "carbon dioxide and water and, using light, produces glucose and oxygen. A common "
    "misconception is that plants only respire at night — in fact they respire all the "
    "time, but photosynthesis dominates during the day."
)

_FAKE_MARKDOWN = (
    "# Session Summary\n\n"
    "## Overview\n"
    "This lecture introduced photosynthesis: how plants convert light energy into "
    "chemical energy, where it happens, and its two main stages.\n\n"
    "## Key Points\n"
    "- Photosynthesis converts light energy into glucose.\n"
    "- It occurs in the chloroplasts, which contain chlorophyll.\n"
    "- Stage 1: light-dependent reactions split water and produce ATP and NADPH.\n"
    "- Stage 2: the Calvin cycle fixes CO2 into glucose.\n\n"
    "## Key Terms\n"
    "- **Chloroplast**: the organelle where photosynthesis occurs.\n"
    "- **Chlorophyll**: the green pigment that absorbs light.\n"
    "- **Calvin cycle**: the carbon-fixation stage.\n\n"
    "## Notable Moments\n"
    "- Clarified the misconception that plants only respire at night.\n"
)


class _FakeTranscriptClient(TranscriptClient):
    def __init__(self, text: str, classroom_id: UUID) -> None:
        self._text = text
        self._classroom_id = classroom_id

    async def fetch(self, session_id: UUID) -> TranscriptDocument:
        return TranscriptDocument(
            session_id=session_id,
            classroom_id=self._classroom_id,
            status="Finalized",
            segment_count=self._text.count(".") or 1,
            text=self._text,
        )


class _FakeRetrieval:
    """Stands in for RetrievalService.search — returns one supporting chunk."""

    async def search(self, request) -> SearchResponse:
        from app.application.dtos.search_dtos import SearchResultItem

        result = ChunkSearchResult(
            chunk_id=uuid4(),
            document_id=uuid4(),
            text="Chlorophyll absorbs mostly red and blue light; it reflects green.",
            score=0.82,
            chunk_index=0,
            metadata={"page": 3},
        )
        return SearchResponse(results=[SearchResultItem.from_result(result)])


class _FakeGenerator(GenerationProvider):
    """Echoes deterministic Markdown; for --long, labels the synthesis output."""

    def __init__(self) -> None:
        self.calls = 0

    async def generate(self, system: str, prompt: str) -> str:
        self.calls += 1
        return _FAKE_MARKDOWN


async def _run_offline(force_long: bool) -> int:
    settings = get_settings()
    classroom_id = uuid4()
    # Repeat the lecture enough to exceed SUMMARY_TRANSCRIPT_MAX_TOKENS so the long run
    # genuinely exercises the map-reduce path (chunk summaries -> synthesis).
    repeats = (settings.summary_transcript_max_tokens * 4 // len(_LECTURE) + 2) if force_long else 1
    transcript = _LECTURE * repeats
    generator = _FakeGenerator()
    service = SummaryGenerator(
        _FakeTranscriptClient(transcript, classroom_id),
        _FakeRetrieval(),  # duck-typed RetrievalService
        generator,
        settings,
    )
    result = await service.generate(uuid4())

    path = "map-reduce" if generator.calls > 1 else "single-pass"
    print(f"# offline summary_check  (model={result.model}, generate() calls="
          f"{generator.calls}, path={path})\n")
    print(result.markdown)
    return 0


async def _run_live(session_id: UUID) -> int:
    from app.application.services.retrieval_service import RetrievalService
    from app.infrastructure.embeddings.ollama_embedding_provider import (
        OllamaEmbeddingProvider,
    )
    from app.infrastructure.generation.ollama_generation_provider import (
        OllamaGenerationProvider,
    )
    from app.infrastructure.live_assistant.transcript_client import (
        LiveAssistantTranscriptClient,
        TranscriptFetchError,
    )
    from app.infrastructure.persistence.chunk_repository import SqlAlchemyChunkRepository
    from app.infrastructure.persistence.database import dispose_engine, get_session_factory

    settings = get_settings()
    generator = OllamaGenerationProvider(
        settings,
        model=settings.summary_model,
        temperature=settings.summary_temperature,
        max_tokens=settings.summary_max_tokens,
    )
    session_factory = get_session_factory()
    try:
        async with session_factory() as session:
            service = SummaryGenerator(
                LiveAssistantTranscriptClient(settings),
                RetrievalService(
                    OllamaEmbeddingProvider(settings),
                    SqlAlchemyChunkRepository(session),
                    default_top_k=settings.search_default_top_k,
                    max_top_k=settings.search_max_top_k,
                ),
                generator,
                settings,
            )
            try:
                result = await service.generate(session_id)
            except TranscriptFetchError as exc:
                print(f"Transcript fetch failed: {exc}", file=sys.stderr)
                return 1
        print(result.markdown)
        return 0
    finally:
        await dispose_engine()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Generate a session summary (S-1).")
    parser.add_argument(
        "--live", metavar="SESSION_ID", type=UUID, default=None,
        help="DEFERRED: fetch + summarize a real session (needs LiveAssistant + Ollama).",
    )
    parser.add_argument(
        "--long", action="store_true",
        help="Offline only: use a long transcript to exercise the map-reduce path.",
    )
    args = parser.parse_args(argv)

    if args.live is not None:
        return asyncio.run(_run_live(args.live))
    return asyncio.run(_run_offline(args.long))


if __name__ == "__main__":
    raise SystemExit(main())
