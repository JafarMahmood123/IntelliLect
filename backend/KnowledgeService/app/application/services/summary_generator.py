from __future__ import annotations

import logging
from uuid import UUID

from app.application.dtos.search_dtos import SearchRequest
from app.application.dtos.summary_dtos import SummaryResult
from app.application.ports.generation_provider import GenerationProvider
from app.application.ports.transcript_client import TranscriptClient
from app.application.services.clock import Clock, SystemClock
from app.application.services.retrieval_service import RetrievalService
from app.application.services.summary_prompts import (
    INSUFFICIENT_CONTENT_MARKDOWN,
    NOTES_SYSTEM_PROMPT,
    SYSTEM_PROMPT,
    build_chunk_prompt,
    build_single_pass_prompt,
    build_synthesis_prompt,
)
from app.application.services.token_counter import HeuristicTokenCounter, TokenCounter

logger = logging.getLogger("knowledge.summary")

# A transcript with fewer real words than this is treated as "insufficient content"
# and short-circuited to a fixed note WITHOUT any model or retrieval calls.
_MIN_TRANSCRIPT_WORDS = 5

# Cap on how much transcript text is used to form the grounding retrieval query.
_GROUNDING_QUERY_MAX_CHARS = 1000


class SummaryGenerationError(RuntimeError):
    """Raised when the model fails to produce a summary.

    Distinct, catchable type so a later phase (S-3) can mark the summary Failed
    regardless of which generation provider raised underneath.
    """


class SummaryGenerator:
    """Turns a lecture transcript into a structured Markdown summary (S-1).

    Flow: fetch transcript -> short-circuit if trivially short -> (optionally) retrieve
    classroom material as SUPPORTING context -> generate Markdown (single pass, or
    map-reduce for long transcripts). The transcript is always the PRIMARY content;
    retrieval only sharpens terminology. Retrieval failures degrade gracefully (summary
    is still produced, ungrounded); generation failures raise SummaryGenerationError.

    Depends only on ports/services, so it runs fully offline against fakes.
    """

    def __init__(
        self,
        transcript_client: TranscriptClient,
        retrieval_service: RetrievalService,
        generation_provider: GenerationProvider,
        settings,
        *,
        token_counter: TokenCounter | None = None,
        clock: Clock | None = None,
    ) -> None:
        self._transcripts = transcript_client
        self._retrieval = retrieval_service
        self._generator = generation_provider
        self._model = settings.summary_model
        self._grounding_enabled = settings.summary_grounding_enabled
        self._grounding_top_k = max(1, settings.summary_grounding_top_k)
        self._transcript_max_tokens = max(1, settings.summary_transcript_max_tokens)
        self._counter = token_counter or HeuristicTokenCounter()
        self._clock = clock or SystemClock()

    async def generate(self, session_id: UUID) -> SummaryResult:
        """Fetch a session's transcript from LiveAssistantService, then summarize it."""
        document = await self._transcripts.fetch(session_id)
        return await self.generate_from_text(
            document.text, document.classroom_id, session_id=session_id
        )

    async def generate_from_text(
        self, transcript: str, classroom_id: UUID, *, session_id: UUID | None = None
    ) -> SummaryResult:
        """Core: summarize a transcript already in hand (no fetch)."""
        cleaned = transcript.strip()
        if self._is_insufficient(cleaned):
            logger.info(
                "Transcript for classroom %s is empty/too short; returning insufficient-"
                "content summary without calling the model.",
                classroom_id,
            )
            return self._result(session_id, classroom_id, INSUFFICIENT_CONTENT_MARKDOWN)

        supporting = await self._retrieve_supporting(classroom_id, cleaned)
        markdown = await self._summarize(cleaned, supporting)
        return self._result(session_id, classroom_id, markdown)

    # -- grounding ------------------------------------------------------------
    async def _retrieve_supporting(
        self, classroom_id: UUID, transcript: str
    ) -> str | None:
        """Retrieve classroom material to ground terminology, or None.

        Config-gated and best-effort: a retrieval failure degrades to an ungrounded
        summary (logged) rather than failing the whole request.
        """
        if not self._grounding_enabled:
            return None
        try:
            request = SearchRequest(
                classroom_id=classroom_id,
                query=transcript[:_GROUNDING_QUERY_MAX_CHARS],
                top_k=self._grounding_top_k,
            )
            response = await self._retrieval.search(request)
        except Exception:  # noqa: BLE001 — grounding is optional; never fail the summary
            logger.warning(
                "Grounding retrieval failed for classroom %s; summarizing WITHOUT "
                "supporting material.",
                classroom_id,
                exc_info=True,
            )
            return None

        if not response.results:
            return None
        return "\n".join(f"- {item.text}" for item in response.results)

    # -- generation -----------------------------------------------------------
    async def _summarize(self, transcript: str, supporting: str | None) -> str:
        """Single pass for short transcripts; map-reduce when over the token cap."""
        if self._counter.count(transcript) <= self._transcript_max_tokens:
            return await self._generate(
                SYSTEM_PROMPT, build_single_pass_prompt(transcript, supporting)
            )
        return await self._map_reduce(transcript, supporting)

    async def _map_reduce(self, transcript: str, supporting: str | None) -> str:
        chunks = self._split_transcript(transcript)
        logger.info("Long transcript: summarizing map-reduce over %d chunks.", len(chunks))
        notes: list[str] = []
        for index, chunk in enumerate(chunks):
            note = await self._generate(
                NOTES_SYSTEM_PROMPT, build_chunk_prompt(chunk, index + 1, len(chunks))
            )
            notes.append(note)
        return await self._generate(
            SYSTEM_PROMPT, build_synthesis_prompt(notes, supporting)
        )

    async def _generate(self, system: str, prompt: str) -> str:
        try:
            return await self._generator.generate(system, prompt)
        except Exception as exc:  # noqa: BLE001 — normalize to one catchable type for S-3
            raise SummaryGenerationError(
                f"Summary generation failed: {exc}"
            ) from exc

    def _split_transcript(self, transcript: str) -> list[str]:
        """Split into word-boundary chunks each within the per-pass token budget."""
        words = transcript.split()
        chunks: list[str] = []
        current: list[str] = []
        for word in words:
            candidate = " ".join(current + [word])
            if current and self._counter.count(candidate) > self._transcript_max_tokens:
                chunks.append(" ".join(current))
                current = [word]
            else:
                current.append(word)
        if current:
            chunks.append(" ".join(current))
        return chunks or [transcript]

    # -- helpers --------------------------------------------------------------
    def _is_insufficient(self, cleaned: str) -> bool:
        return len(cleaned.split()) < _MIN_TRANSCRIPT_WORDS

    def _result(
        self, session_id: UUID | None, classroom_id: UUID, markdown: str
    ) -> SummaryResult:
        return SummaryResult(
            session_id=session_id,
            classroom_id=classroom_id,
            markdown=markdown,
            model=self._model,
            generated_at=self._clock.now(),
        )
