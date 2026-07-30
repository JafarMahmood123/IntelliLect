"""GenerationProvider backed by Google's Gemini generateContent API.

WHY THIS EXISTS. The only generation path was a local Ollama model. On this platform that
means qwen2.5:7b-instruct on the host CPU, sharing an 8-core laptop with LiveKit egress and
Whisper STT — and a session summary is the single largest generation the system performs
(a whole lecture transcript, map-reduced). Hosted generation removes the model from the
critical resource that the live session is already competing for.

Mirrors GeminiEmbeddingProvider: same key, same base URL, same error vocabulary. The wire
format matches LiveAssistantService's brain client, so all three Gemini call sites in the
platform look alike.

THINKING TOKENS COUNT AGAINST maxOutputTokens. On the 3.x models a request can burn its
entire budget reasoning and return a candidate with NO text and finishReason=MAX_TOKENS —
which reads as "the model returned nothing" rather than "the cap was too low". Summaries
are the worst case for this because the budget is sized for prose, not for prose plus
reasoning. Hence ``thinking_level`` (blank omits thinkingConfig entirely, which is required
for non-3.x models that reject the field) and the explicit MAX_TOKENS diagnosis below.
"""

from __future__ import annotations

import logging

import httpx

from app.application.ports.generation_provider import GenerationProvider
from app.infrastructure.config.settings import Settings

logger = logging.getLogger("knowledge.generation")


class GeminiGenerationError(RuntimeError):
    """Raised when the Gemini generation API cannot produce a completion.

    Actionable for an operator (is the key set? is the model name live? was the budget too
    small?) rather than surfacing a raw transport error or an empty string.
    """


class GeminiGenerationProvider(GenerationProvider):
    """GenerationProvider over Gemini's ``generateContent``."""

    def __init__(
        self,
        settings: Settings,
        *,
        model: str | None = None,
        temperature: float | None = None,
        max_tokens: int | None = None,
        timeout: float | None = None,
        thinking_level: str | None = None,
        transport: httpx.BaseTransport | None = None,
    ) -> None:
        # Overrides mirror OllamaGenerationProvider so one use case (summaries) can run its
        # own model / temperature / budget without a second implementation.
        self._base_url = settings.gemini_base_url.rstrip("/")
        self._model = model or settings.gemini_generation_model
        self._api_key = settings.gemini_api_key
        self._timeout = timeout or settings.generation_timeout_seconds
        self._temperature = (
            temperature if temperature is not None else settings.generation_temperature
        )
        self._max_tokens = max_tokens or settings.generation_max_tokens
        self._thinking_level = (
            thinking_level
            if thinking_level is not None
            else settings.gemini_thinking_level
        ).strip()
        self._transport = transport
        if not self._api_key:
            logger.warning(
                "GEMINI_API_KEY is empty; Gemini generation calls will fail until it is set."
            )

    def _generation_config(self) -> dict:
        config: dict = {
            "temperature": self._temperature,
            "maxOutputTokens": self._max_tokens,
        }
        # Blank omits the field: models before 3.x reject thinkingConfig outright.
        if self._thinking_level:
            config["thinkingConfig"] = {"thinkingLevel": self._thinking_level}
        return config

    async def generate(self, system: str, prompt: str) -> str:
        url = f"{self._base_url}/models/{self._model}:generateContent"
        payload = {
            "systemInstruction": {"parts": [{"text": system}]},
            "contents": [{"role": "user", "parts": [{"text": prompt}]}],
            "generationConfig": self._generation_config(),
        }
        # Key travels in a header, not the URL, so it never lands in request logs.
        headers = {"x-goog-api-key": self._api_key, "Content-Type": "application/json"}

        try:
            async with httpx.AsyncClient(
                timeout=self._timeout, transport=self._transport
            ) as client:
                response = await client.post(url, json=payload, headers=headers)
        except httpx.RequestError as exc:
            raise GeminiGenerationError(
                f"Could not reach the Gemini API at {self._base_url}. Check network/"
                f"GEMINI_BASE_URL. Original error: {exc}"
            ) from exc

        self._raise_for_status(response)
        return self._extract_text(response.json())

    def _raise_for_status(self, response: httpx.Response) -> None:
        if response.status_code in (401, 403):
            raise GeminiGenerationError(
                "Gemini rejected the generation request (check GEMINI_API_KEY). "
                f"HTTP {response.status_code}: {response.text[:200]}"
            )
        if response.status_code == 404:
            raise GeminiGenerationError(
                f"Gemini has no model '{self._model}'. Pinned versions go quota-zero or "
                f"404 over time — prefer a *-latest alias. Detail: {response.text[:200]}"
            )
        if response.status_code == 429:
            raise GeminiGenerationError(
                "Gemini generation rate limit hit (429). Retry later or lower throughput. "
                f"Detail: {response.text[:200]}"
            )
        if response.status_code >= 400:
            raise GeminiGenerationError(
                f"Gemini generateContent failed with HTTP {response.status_code}: "
                f"{response.text[:300]}"
            )

    def _extract_text(self, data: dict) -> str:
        candidates = data.get("candidates") or []
        if not candidates:
            # No candidates at all => the prompt itself was blocked. Name the reason if the
            # API gave one, because "no output" is otherwise indistinguishable from a bug.
            reason = (data.get("promptFeedback") or {}).get("blockReason")
            raise GeminiGenerationError(
                "Gemini returned no candidates"
                + (f" (blockReason={reason})" if reason else " (possibly safety-blocked)")
                + f" for model '{self._model}'."
            )

        candidate = candidates[0] or {}
        parts = (candidate.get("content") or {}).get("parts") or []
        text = "".join(part.get("text") or "" for part in parts).strip()
        if text:
            return text

        finish = candidate.get("finishReason")
        if finish == "MAX_TOKENS":
            # The specific trap this class documents: budget spent before any prose emerged.
            raise GeminiGenerationError(
                f"Gemini hit maxOutputTokens ({self._max_tokens}) before producing any "
                "text. Thinking tokens are charged against that budget — raise the token "
                "cap for this use case, or lower GEMINI_THINKING_LEVEL."
            )
        raise GeminiGenerationError(
            f"Gemini returned an empty completion for model '{self._model}' "
            f"(finishReason={finish})."
        )
