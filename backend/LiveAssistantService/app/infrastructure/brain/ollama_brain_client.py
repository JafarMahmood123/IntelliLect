"""BrainClient backed by a LOCAL generative model in host Ollama.

Calls ``POST /api/chat`` (stream:false) with this service's own grounded,
silence-biased evaluation prompt and parses the reply as STRICT JSON (via the shared
``outcome_parser``). No model weights live in this container. Mirrors KnowledgeService's
generation provider (optional bearer auth, configurable timeout, clear errors).

Degradation policy: a transport/HTTP failure raises ``OllamaBrainError`` (the caller
degrades to "no feedback"); a model reply that is not valid JSON degrades to "no
feedback" in ``outcome_parser``, so a chatty or malformed model can never crash the live loop.
"""

from __future__ import annotations

import logging

import httpx

from app.application.ports.brain_client import BrainClient
from app.domain.evaluation.evaluation_outcome import EvaluationOutcome
from app.domain.evaluation.retrieved_chunk import RetrievedChunk
from app.domain.idea.completed_idea import CompletedIdea
from app.infrastructure.brain.evaluation_prompt import (
    SMOKE_SYSTEM_PROMPT,
    SYSTEM_PROMPT,
    build_numbered_context,
    build_user_prompt,
)
from app.infrastructure.brain.outcome_parser import parse_outcome
from app.infrastructure.brain.quiz_parser import parse_quiz
from app.infrastructure.brain.quiz_prompt import (
    SYSTEM_PROMPT as QUIZ_SYSTEM_PROMPT,
    build_response_schema,
    build_user_prompt as build_quiz_user_prompt,
)
from app.domain.quiz.generated_quiz import GeneratedQuiz
from app.infrastructure.config.settings import Settings

logger = logging.getLogger("liveassistant.brain")


class OllamaBrainError(RuntimeError):
    """Raised when the local Ollama server cannot produce a completion (transport/HTTP)."""


class OllamaBrainClient(BrainClient):
    def __init__(self, settings: Settings) -> None:
        self._base_url = settings.ollama_base_url.rstrip("/")
        self._model = settings.eval_model
        self._timeout = settings.eval_timeout_seconds
        self._temperature = settings.eval_temperature
        self._max_tokens = settings.eval_max_tokens
        self._num_thread = settings.eval_num_thread
        # LA-7: confidence used when the model omits it (backward-compatible).
        self._default_confidence = settings.feedback_default_confidence
        self._quiz_max_tokens = settings.quiz_max_tokens
        self._quiz_timeout = settings.quiz_timeout_seconds
        self._quiz_temperature = settings.quiz_temperature
        self._headers: dict[str, str] = {}
        if settings.ollama_auth_token:
            self._headers["Authorization"] = f"Bearer {settings.ollama_auth_token}"

    async def evaluate(
        self, idea: CompletedIdea, chunks: list[RetrievedChunk]
    ) -> EvaluationOutcome:
        context, citation_map = build_numbered_context(chunks)
        content = await self._complete(SYSTEM_PROMPT, build_user_prompt(idea.text, context))
        return parse_outcome(content, citation_map, self._default_confidence)

    async def generate_quiz(
        self,
        idea_text: str,
        chunks: list[RetrievedChunk],
        *,
        question_count: int,
        min_options: int,
        max_options: int,
    ) -> GeneratedQuiz | None:
        context, citation_map = build_numbered_context(chunks)
        content = await self._complete(
            QUIZ_SYSTEM_PROMPT,
            build_quiz_user_prompt(idea_text, context, question_count),
            # Ollama takes a JSON Schema in `format` and constrains decoding to it, so the shape
            # is enforced during generation the same way Gemini's responseSchema does.
            response_format=build_response_schema(
                max_questions=question_count,
                min_options=min_options,
                max_options=max_options,
            ),
            temperature=self._quiz_temperature,
            max_tokens=self._quiz_max_tokens,
            timeout=self._quiz_timeout,
        )
        return parse_quiz(
            content,
            max_questions=question_count,
            min_options=min_options,
            max_options=max_options,
            citation_numbers=set(citation_map),
            grounded=bool(chunks),
        )

    async def smoke_complete(self, transcript_text: str) -> str:
        """SMOKE-TEST ONLY (temporary): raw transcript -> LLM -> raw reply."""
        return await self._complete(SMOKE_SYSTEM_PROMPT, transcript_text)

    async def _complete(
        self,
        system: str,
        user: str,
        *,
        response_format: dict | None = None,
        temperature: float | None = None,
        max_tokens: int | None = None,
        timeout: float | None = None,
    ) -> str:
        """POST the chat request and return the model's message content.

        The only blocking work is a remote HTTP call (Ollama runs the model), already
        awaited off the loop by httpx; JSON parsing is negligible.
        """
        url = f"{self._base_url}/api/chat"
        payload = {
            "model": self._model,
            "messages": [
                {"role": "system", "content": system},
                {"role": "user", "content": user},
            ],
            "stream": False,
            "options": {
                "temperature": self._temperature if temperature is None else temperature,
                "num_predict": self._max_tokens if max_tokens is None else max_tokens,
            },
        }
        if response_format is not None:
            payload["format"] = response_format
        # Bound generation to a core budget so it doesn't fight STT for all 8 cores
        # (0 => omit and let Ollama use its default = all cores). See settings.eval_num_thread.
        if self._num_thread:
            payload["options"]["num_thread"] = self._num_thread
        try:
            async with httpx.AsyncClient(
                timeout=timeout or self._timeout, headers=self._headers
            ) as client:
                response = await client.post(url, json=payload)
        except httpx.RequestError as exc:
            raise OllamaBrainError(
                f"Could not reach Ollama at {self._base_url}. Ensure it is running and "
                f"OLLAMA_BASE_URL is correct. Original error: {exc}"
            ) from exc

        if response.status_code == 404:
            raise OllamaBrainError(
                f"Ollama does not have model '{self._model}'. Pull it first with "
                f"`ollama pull {self._model}`."
            )
        if response.status_code >= 400:
            raise OllamaBrainError(
                f"Ollama /api/chat failed with HTTP {response.status_code}: {response.text}"
            )
        return (response.json().get("message") or {}).get("content", "")
