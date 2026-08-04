from __future__ import annotations

import json
from collections.abc import AsyncIterator

import httpx

from app.application.ports.generation_provider import GenerationProvider
from app.infrastructure.config.settings import Settings


class OllamaGenerationError(RuntimeError):
    """Raised when the local Ollama server cannot produce a completion.

    The message is actionable for an operator (is Ollama running? is the generative
    model pulled?) rather than surfacing a raw transport error.
    """


class OllamaGenerationProvider(GenerationProvider):
    """GenerationProvider backed by a LOCAL Ollama server over HTTP.

    No model weights live in this container — every call is an HTTP request to the
    host's Ollama (`POST /api/chat`). Mirrors OllamaEmbeddingProvider: optional
    bearer auth, configurable timeout, and clear errors.
    """

    def __init__(
        self,
        settings: Settings,
        *,
        model: str | None = None,
        temperature: float | None = None,
        max_tokens: int | None = None,
        timeout: float | None = None,
    ) -> None:
        # Defaults come from the Phase 10 answering config; the optional overrides let
        # another use case (e.g. S-1 summarization) run the same client with its own
        # model / temperature / token budget without a second implementation.
        self._base_url = settings.ollama_base_url.rstrip("/")
        self._model = model or settings.generation_model
        self._timeout = timeout or settings.generation_timeout_seconds
        self._temperature = (
            temperature if temperature is not None else settings.generation_temperature
        )
        self._max_tokens = max_tokens or settings.generation_max_tokens
        self._headers: dict[str, str] = {}
        if settings.ollama_auth_token:
            self._headers["Authorization"] = f"Bearer {settings.ollama_auth_token}"

    def _messages(self, system: str, prompt: str) -> list[dict[str, str]]:
        return [
            {"role": "system", "content": system},
            {"role": "user", "content": prompt},
        ]

    def _options(self) -> dict[str, float | int]:
        return {"temperature": self._temperature, "num_predict": self._max_tokens}

    async def generate(self, system: str, prompt: str) -> str:
        url = f"{self._base_url}/api/chat"
        payload = {
            "model": self._model,
            "messages": self._messages(system, prompt),
            "stream": False,
            "options": self._options(),
        }
        try:
            async with httpx.AsyncClient(
                timeout=self._timeout, headers=self._headers
            ) as client:
                response = await client.post(url, json=payload)
        except httpx.RequestError as exc:
            raise OllamaGenerationError(
                f"Could not reach Ollama at {self._base_url}. Ensure it is running "
                f"and OLLAMA_BASE_URL is correct. Original error: {exc}"
            ) from exc

        self._raise_for_status(response)
        data = response.json()
        content = (data.get("message") or {}).get("content")
        if not content:
            raise OllamaGenerationError(
                f"Ollama returned no content. Confirm '{self._model}' is a chat model "
                f"and has been pulled (`ollama pull {self._model}`). Response: {data}"
            )
        return content

    async def generate_stream(self, system: str, prompt: str) -> AsyncIterator[str]:
        """Optional streaming path: yield content deltas as they arrive.

        Non-stream `generate()` is the primary path; this is provided for future
        user-facing streaming and is exercised live only.
        """
        url = f"{self._base_url}/api/chat"
        payload = {
            "model": self._model,
            "messages": self._messages(system, prompt),
            "stream": True,
            "options": self._options(),
        }
        try:
            async with httpx.AsyncClient(
                timeout=self._timeout, headers=self._headers
            ) as client:
                async with client.stream("POST", url, json=payload) as response:
                    self._raise_for_status(response)
                    async for line in response.aiter_lines():
                        if not line.strip():
                            continue
                        delta = (json.loads(line).get("message") or {}).get("content")
                        if delta:
                            yield delta
        except httpx.RequestError as exc:
            raise OllamaGenerationError(
                f"Could not reach Ollama at {self._base_url}. Original error: {exc}"
            ) from exc

    def _raise_for_status(self, response: httpx.Response) -> None:
        if response.status_code == 404:
            raise OllamaGenerationError(
                f"Ollama does not have model '{self._model}'. Pull it first with "
                f"`ollama pull {self._model}`."
            )
        if response.status_code >= 400:
            raise OllamaGenerationError(
                f"Ollama /api/chat failed with HTTP {response.status_code}: "
                f"{response.text}"
            )
