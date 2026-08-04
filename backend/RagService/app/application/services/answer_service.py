from __future__ import annotations

import logging
from uuid import UUID

from app.application.dtos.answer_dtos import AnswerResponse, AnswerSource
from app.application.dtos.search_dtos import SearchRequest
from app.application.ports.generation_provider import GenerationProvider
from app.application.services.answer_prompts import (
    NO_CONTEXT_ANSWER,
    SYSTEM_PROMPT,
    build_user_prompt,
)
from app.application.services.context_builder import Citation, ContextBuilder
from app.application.services.retrieval_service import RetrievalService

logger = logging.getLogger("knowledge.answer")


class AnswerService:
    """Retrieval-augmented answering over one classroom's materials.

    Retrieve (Phase 7, classroom-scoped) -> pack a numbered context -> generate a
    grounded, citation-aware answer. The classroom filter is applied entirely by
    RetrievalService and is never bypassed. When retrieval finds nothing, the model
    is NOT called — a fixed "no relevant material" answer is returned.
    """

    def __init__(
        self,
        retrieval_service: RetrievalService,
        context_builder: ContextBuilder,
        generation_provider: GenerationProvider,
        default_top_k: int = 6,
    ) -> None:
        self._retrieval = retrieval_service
        self._context = context_builder
        self._generator = generation_provider
        self._default_top_k = max(1, default_top_k)

    async def answer(
        self, classroom_id: UUID, question: str, top_k: int | None = None
    ) -> AnswerResponse:
        cleaned = question.strip()
        if not cleaned:
            raise ValueError("question must not be empty")

        request = SearchRequest(
            classroom_id=classroom_id,
            query=cleaned,
            top_k=top_k or self._default_top_k,
        )
        search = await self._retrieval.search(request)

        if not search.results:
            logger.info(
                "No relevant chunks for classroom %s; returning no-context answer.",
                classroom_id,
            )
            return AnswerResponse(answer=NO_CONTEXT_ANSWER, sources=[])

        packed = self._context.build(search.results)
        user_prompt = build_user_prompt(packed.text, cleaned)
        answer_text = await self._generator.generate(SYSTEM_PROMPT, user_prompt)

        sources = [self._to_source(citation) for citation in packed.citations]
        logger.info(
            "Answered classroom %s using %d source(s).", classroom_id, len(sources)
        )
        return AnswerResponse(answer=answer_text, sources=sources)

    @staticmethod
    def _to_source(citation: Citation) -> AnswerSource:
        item = citation.item
        metadata = item.metadata or {}
        return AnswerSource(
            citation=citation.number,
            chunk_id=item.chunk_id,
            document_id=item.document_id,
            page=metadata.get("page"),
            slide=metadata.get("slide"),
            section=metadata.get("section"),
            score=item.score,
        )
