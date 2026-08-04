from __future__ import annotations

from uuid import UUID

import httpx

from app.application.dtos.summary_dtos import TranscriptDocument
from app.application.ports.transcript_client import TranscriptClient
from app.infrastructure.config.settings import Settings


class TranscriptFetchError(RuntimeError):
    """Raised when the session transcript cannot be fetched from LiveAssistantService.

    The message is actionable for an operator (is LiveAssistantService reachable? is
    LIVE_ASSISTANT_BASE_URL / INTERNAL_API_SECRET correct? does the session exist?)
    rather than surfacing a raw transport error.
    """


class LiveAssistantTranscriptClient(TranscriptClient):
    """TranscriptClient over LiveAssistantService's S-0 internal endpoint.

    Mirrors the OllamaEmbeddingProvider HTTP style: an httpx call per request with a
    configurable timeout and the shared internal secret header. No model weights or
    persistent connections. ``transport`` is injectable so tests can drive it with an
    ``httpx.MockTransport`` without a live server.
    """

    def __init__(
        self, settings: Settings, *, transport: httpx.BaseTransport | None = None
    ) -> None:
        self._base_url = settings.live_assistant_base_url.rstrip("/")
        self._timeout = settings.generation_timeout_seconds
        self._transport = transport
        self._headers: dict[str, str] = {}
        if settings.internal_api_secret:
            self._headers["X-Internal-Secret"] = settings.internal_api_secret

    async def fetch(self, session_id: UUID) -> TranscriptDocument:
        if not self._base_url:
            raise TranscriptFetchError(
                "LIVE_ASSISTANT_BASE_URL is not configured; cannot fetch the transcript."
            )
        url = f"{self._base_url}/api/internal/sessions/{session_id}/transcript"
        try:
            async with httpx.AsyncClient(
                timeout=self._timeout, headers=self._headers, transport=self._transport
            ) as client:
                response = await client.get(url)
        except httpx.RequestError as exc:
            raise TranscriptFetchError(
                f"Could not reach LiveAssistantService at {self._base_url}. Ensure it is "
                f"running and LIVE_ASSISTANT_BASE_URL is correct. Original error: {exc}"
            ) from exc

        if response.status_code == 404:
            raise TranscriptFetchError(f"No transcript for session {session_id} (404).")
        if response.status_code >= 400:
            raise TranscriptFetchError(
                f"Transcript fetch failed with HTTP {response.status_code}: {response.text}"
            )

        return self._to_document(response.json())

    @staticmethod
    def _to_document(data: dict) -> TranscriptDocument:
        try:
            return TranscriptDocument(
                session_id=UUID(str(data["sessionId"])),
                classroom_id=UUID(str(data["classroomId"])),
                status=str(data.get("status", "")),
                segment_count=int(data.get("segmentCount", 0)),
                text=str(data.get("text", "")),
            )
        except (KeyError, ValueError, TypeError) as exc:
            raise TranscriptFetchError(
                f"Malformed transcript payload from LiveAssistantService: {data!r}"
            ) from exc
