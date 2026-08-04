from __future__ import annotations

import asyncio
import logging
import math
import time
from uuid import UUID

from app.application.dtos.search_dtos import (
    SearchRequest,
    SearchResponse,
    SearchResultItem,
)
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
from app.observability import metrics

logger = logging.getLogger("knowledge.summary")

# A transcript with fewer real words than this is treated as "insufficient content"
# and short-circuited to a fixed note WITHOUT any model or retrieval calls.
_MIN_TRANSCRIPT_WORDS = 5

# Cap on how much transcript text forms ONE grounding retrieval query. Several such
# windows are sampled across the transcript (summary_grounding_query_windows).
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
        # summary_model names the OLLAMA model; on Gemini the label would otherwise
        # record "qwen2.5:7b-instruct" as the author of every hosted summary.
        self._model = (
            settings.gemini_summary_model
            if settings.generation_provider.strip().lower() == "gemini"
            else settings.summary_model
        )
        self._grounding_enabled = settings.summary_grounding_enabled
        self._grounding_top_k = max(1, settings.summary_grounding_top_k)
        self._grounding_windows = max(1, settings.summary_grounding_query_windows)
        self._grounding_max_chunks = max(1, settings.summary_grounding_max_chunks)
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
        log_extra = {
            "session_id": str(session_id) if session_id else None,
            "model": self._model,
        }
        logger.info("summary_generation_started", extra=log_extra)

        cleaned = transcript.strip()
        if self._is_insufficient(cleaned):
            logger.info(
                "Transcript for classroom %s is empty/too short; returning insufficient-"
                "content summary without calling the model.",
                classroom_id,
            )
            logger.info(
                "summary_generation_finished",
                extra={**log_extra, "duration_ms": 0, "grounded": False},
            )
            return self._result(session_id, classroom_id, INSUFFICIENT_CONTENT_MARKDOWN)

        metrics.observe_summary_transcript_tokens(self._counter.count(cleaned))
        started = time.perf_counter()
        supporting = await self._retrieve_supporting(classroom_id, cleaned)
        markdown = await self._summarize(cleaned, supporting)
        elapsed = time.perf_counter() - started
        metrics.observe_summary_generation(elapsed)
        if supporting is not None:
            metrics.record_summary_grounded()
        logger.info(
            "summary_generation_finished",
            extra={
                **log_extra,
                "duration_ms": round(elapsed * 1000),
                "grounded": supporting is not None,
            },
        )
        return self._result(session_id, classroom_id, markdown)

    # -- grounding ------------------------------------------------------------
    async def _retrieve_supporting(
        self, classroom_id: UUID, transcript: str
    ) -> str | None:
        """Retrieve classroom material to ground the summary, or None.

        Queries several excerpts spanning the whole transcript, not just its opening, so
        material for late-lecture topics is retrieved too — the summary can only correct
        a claim against material it actually pulled in.

        Config-gated and best-effort: retrieval failures degrade to a partially- or
        un-grounded summary (logged) rather than failing the whole request.
        """
        if not self._grounding_enabled:
            return None

        windows = self._query_windows(transcript)
        # return_exceptions: one dead window still leaves the others usable — grounding on
        # 3 of 4 excerpts beats discarding all of it.
        responses = await asyncio.gather(
            *(
                self._retrieval.search(
                    SearchRequest(
                        classroom_id=classroom_id,
                        query=window,
                        top_k=self._grounding_top_k,
                    )
                )
                for window in windows
            ),
            return_exceptions=True,
        )

        failures = [r for r in responses if isinstance(r, BaseException)]
        for failure in failures:
            # return_exceptions captures CancelledError too; swallowing it here would
            # make the summary task ignore shutdown.
            if isinstance(failure, asyncio.CancelledError):
                raise failure
        if failures:
            logger.warning(
                "Grounding retrieval failed for %d of %d transcript window(s) in "
                "classroom %s; summarizing with whatever material was retrieved.",
                len(failures),
                len(windows),
                classroom_id,
                exc_info=failures[0],
            )

        merged = self._merge_grounding(
            [r for r in responses if not isinstance(r, BaseException)]
        )
        if not merged:
            return None
        return "\n".join(f"- {item.text}" for item in merged)

    def _query_windows(self, transcript: str) -> list[str]:
        """Evenly spaced excerpts of the transcript to use as retrieval queries."""
        if len(transcript) <= _GROUNDING_QUERY_MAX_CHARS:
            return [transcript]
        count = min(
            self._grounding_windows,
            math.ceil(len(transcript) / _GROUNDING_QUERY_MAX_CHARS),
        )
        if count == 1:
            return [transcript[:_GROUNDING_QUERY_MAX_CHARS]]
        # First window starts at 0, last ENDS at the transcript's end, so the closing
        # material — often the announcements and the final worked example — is covered.
        span = len(transcript) - _GROUNDING_QUERY_MAX_CHARS
        return [
            transcript[start : start + _GROUNDING_QUERY_MAX_CHARS]
            for start in (round(i * span / (count - 1)) for i in range(count))
        ]

    def _merge_grounding(
        self, responses: list[SearchResponse]
    ) -> list[SearchResultItem]:
        """Union the per-window hits: best score per chunk, strongest first, capped.

        Windows overlap in what they retrieve, so de-duplication is what keeps the
        supporting block from repeating the same chunk several times.
        """
        best: dict[UUID, SearchResultItem] = {}
        for response in responses:
            for item in response.results:
                existing = best.get(item.chunk_id)
                if existing is None or item.score > existing.score:
                    best[item.chunk_id] = item
        ordered = sorted(best.values(), key=lambda item: item.score, reverse=True)
        return ordered[: self._grounding_max_chunks]

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
