"""RetrievalClient backed by the existing RagService over HTTP.

RagService owns the vector DB (Phase 7). We send the idea TEXT as the query to
``POST /api/search`` (secured by the shared ``INTERNAL_API_SECRET``); it embeds and
searches internally and returns the top-k chunks. This service never touches
RagService's database directly.
"""

from __future__ import annotations

from typing import Any
from uuid import UUID

import httpx

from app.application.ports.retrieval_client import RetrievalClient
from app.domain.evaluation.retrieved_chunk import RetrievedChunk
from app.infrastructure.config.settings import Settings


class RetrievalError(RuntimeError):
    """Raised when RagService retrieval fails (transport or HTTP error).

    The message is actionable for an operator (is RagService reachable? is the
    internal secret correct?). The caller (IdeaEvaluator) catches this and degrades to
    "no feedback" so a failed retrieval never breaks the live loop.
    """


class RagRetrievalClient(RetrievalClient):
    def __init__(
        self, settings: Settings, *, transport: httpx.BaseTransport | None = None
    ) -> None:
        self._base_url = settings.knowledge_base_url.rstrip("/")
        self._secret = settings.internal_api_secret
        self._timeout = settings.eval_timeout_seconds
        # transport is injectable so tests can assert the exact request via MockTransport.
        self._transport = transport

    async def retrieve(
        self, classroom_id: UUID, query_text: str, top_k: int
    ) -> list[RetrievedChunk]:
        url = f"{self._base_url}/api/search"
        headers = {"X-Internal-Secret": self._secret}
        payload = {"classroomId": str(classroom_id), "query": query_text, "topK": top_k}
        try:
            async with httpx.AsyncClient(
                timeout=self._timeout, transport=self._transport
            ) as client:
                response = await client.post(url, json=payload, headers=headers)
        except httpx.RequestError as exc:
            raise RetrievalError(
                f"Could not reach RagService at {self._base_url}. Ensure it is "
                f"running and RAG_BASE_URL is correct. Original error: {exc}"
            ) from exc

        if response.status_code == 401:
            raise RetrievalError(
                "RagService rejected the internal secret (401). Check "
                "INTERNAL_API_SECRET matches RagService."
            )
        if response.status_code >= 400:
            raise RetrievalError(
                f"RagService /api/search failed with HTTP "
                f"{response.status_code}: {response.text}"
            )

        results = response.json().get("results", [])
        return [self._to_chunk(item) for item in results]

    @staticmethod
    def _to_chunk(item: dict[str, Any]) -> RetrievedChunk:
        # RagService returns camelCase; page/slide/section live under metadata.
        metadata = item.get("metadata") or {}
        return RetrievedChunk(
            text=item.get("text", ""),
            score=float(item.get("score", 0.0)),
            chunk_id=UUID(str(item["chunkId"])),
            document_id=UUID(str(item["documentId"])),
            page=metadata.get("page"),
            slide=metadata.get("slide"),
            section=metadata.get("section"),
        )
