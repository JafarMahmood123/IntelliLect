"""BrainClient backed by Google's Gemini API (Google AI Studio).

Everything that might change between deployments is configuration, not code:
  - ``GEMINI_MODEL``      — which model to call (e.g. gemini-2.0-flash, gemini-2.5-flash);
  - ``GEMINI_API_KEY``    — the secret key (keep it in .env, never in git/compose);
  - ``GEMINI_BASE_URL``   — the API endpoint (for a different API version or a proxy);
  - ``EVAL_TEMPERATURE`` / ``EVAL_MAX_TOKENS`` / ``EVAL_TIMEOUT_SECONDS`` — generation params;
  - ``GEMINI_GENERATION_CONFIG_JSON`` — a JSON object merged into the request's ``generationConfig``,
    so extra request fields (topP, topK, stopSequences, …) can be attached WITHOUT a code change.

Contract matches the other BrainClients: ``evaluate`` returns a structured outcome (degrading to
no-feedback on a malformed/blocked reply, via the shared ``outcome_parser``); ``smoke_complete``
returns raw text. Transport/HTTP failures raise ``GeminiBrainError`` for the caller to degrade.
"""

from __future__ import annotations

import json
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
from app.infrastructure.brain.quiz_parser import parse_answers, parse_quiz
from app.infrastructure.brain.quiz_prompt import (
    ANSWERS_SYSTEM_PROMPT,
    SYSTEM_PROMPT as QUIZ_SYSTEM_PROMPT,
    build_answers_response_schema,
    build_answers_user_prompt,
    build_response_schema,
    build_user_prompt as build_quiz_user_prompt,
)
from app.domain.quiz.generated_quiz import GeneratedQuestion, GeneratedQuiz
from app.infrastructure.config.settings import Settings

logger = logging.getLogger("liveassistant.brain")


class GeminiBrainError(RuntimeError):
    """Raised when the Gemini API cannot produce a completion (transport/HTTP/auth)."""


class GeminiBrainClient(BrainClient):
    def __init__(self, settings: Settings) -> None:
        self._base_url = settings.gemini_base_url.rstrip("/")
        self._model = settings.gemini_model
        self._api_key = settings.gemini_api_key
        self._timeout = settings.eval_timeout_seconds
        self._temperature = settings.eval_temperature
        self._max_tokens = settings.eval_max_tokens
        self._default_confidence = settings.feedback_default_confidence
        self._quiz_max_tokens = settings.quiz_max_tokens
        self._quiz_timeout = settings.quiz_timeout_seconds
        self._quiz_temperature = settings.quiz_temperature
        # Extra generationConfig fields merged into every request (topP, topK, …). Config-driven,
        # so new request fields never need a code change.
        self._extra_generation_config = _parse_json_object(
            settings.gemini_generation_config_json, "GEMINI_GENERATION_CONFIG_JSON"
        )
        if not self._api_key:
            logger.warning(
                "GEMINI_API_KEY is empty; Gemini calls will fail until it is set (put it in .env)."
            )

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
        avoid: list[str] | None = None,
    ) -> GeneratedQuiz | None:
        context, citation_map = build_numbered_context(chunks)
        schema = build_response_schema(
            max_questions=question_count,
            min_options=min_options,
            max_options=max_options,
        )
        content = await self._complete(
            QUIZ_SYSTEM_PROMPT,
            build_quiz_user_prompt(idea_text, context, question_count, avoid),
            # Constrained decoding: Gemini generates AGAINST the schema, so a wrong shape is
            # prevented rather than detected. The parser still runs — see quiz_parser.
            generation_config={
                "temperature": self._quiz_temperature,
                "maxOutputTokens": self._quiz_max_tokens,
                "responseMimeType": "application/json",
                "responseSchema": _to_gemini_schema(schema),
            },
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

    async def generate_answers(
        self,
        question_text: str,
        idea_text: str,
        chunks: list[RetrievedChunk],
        *,
        min_options: int,
        max_options: int,
    ) -> GeneratedQuestion | None:
        context, _ = build_numbered_context(chunks)
        schema = build_answers_response_schema(
            min_options=min_options, max_options=max_options
        )
        content = await self._complete(
            ANSWERS_SYSTEM_PROMPT,
            build_answers_user_prompt(question_text, idea_text, context, max_options),
            generation_config={
                "temperature": self._quiz_temperature,
                "maxOutputTokens": self._quiz_max_tokens,
                "responseMimeType": "application/json",
                "responseSchema": _to_gemini_schema(schema),
            },
            timeout=self._quiz_timeout,
        )
        return parse_answers(
            content, question_text, min_options=min_options, max_options=max_options
        )

    async def smoke_complete(self, transcript_text: str) -> str:
        """SMOKE-TEST ONLY (temporary): raw transcript -> Gemini -> raw reply."""
        return await self._complete(SMOKE_SYSTEM_PROMPT, transcript_text)

    async def _complete(
        self,
        system: str,
        user: str,
        *,
        generation_config: dict | None = None,
        timeout: float | None = None,
    ) -> str:
        """POST a generateContent request and return the model's text.

        The only blocking work is the remote HTTP call, awaited off the loop by httpx.
        ``generation_config`` overrides the evaluation defaults wholesale (quiz generation needs
        its own cap and a response schema); the env-provided extras are merged on top either way.
        """
        url = f"{self._base_url}/models/{self._model}:generateContent"
        generation_config = dict(
            generation_config
            or {"temperature": self._temperature, "maxOutputTokens": self._max_tokens}
        )
        generation_config.update(self._extra_generation_config)
        payload = {
            "systemInstruction": {"parts": [{"text": system}]},
            "contents": [{"role": "user", "parts": [{"text": user}]}],
            "generationConfig": generation_config,
        }
        # Key travels in a header (not the URL), so it never lands in request logs.
        headers = {"x-goog-api-key": self._api_key, "Content-Type": "application/json"}

        try:
            async with httpx.AsyncClient(timeout=timeout or self._timeout) as client:
                response = await client.post(url, json=payload, headers=headers)
        except httpx.RequestError as exc:
            raise GeminiBrainError(
                f"Could not reach the Gemini API at {self._base_url}. Check network/GEMINI_BASE_URL. "
                f"Original error: {exc}"
            ) from exc

        if response.status_code in (401, 403) or (
            response.status_code == 400 and "API_KEY" in response.text
        ):
            raise GeminiBrainError(
                "Gemini rejected the request (check GEMINI_API_KEY and that billing/free-tier is "
                f"active). HTTP {response.status_code}: {response.text}"
            )
        if response.status_code >= 400:
            raise GeminiBrainError(
                f"Gemini generateContent failed with HTTP {response.status_code}: {response.text}"
            )
        return _extract_text(response.json())


def _to_gemini_schema(schema: dict) -> dict:
    """Translate the canonical JSON Schema into the dialect Gemini's ``responseSchema`` expects.

    Gemini's Schema is an OpenAPI-derived proto whose ``type`` is an ENUM, and proto JSON requires
    the enum NAME — "OBJECT", not "object". The canonical schema stays lowercase because that is
    what standard JSON Schema (and therefore Ollama) uses, so the uppercasing lives here rather
    than forking the schema per provider.
    """
    converted: dict = {}
    for key, value in schema.items():
        if key == "type" and isinstance(value, str):
            converted[key] = value.upper()
        elif key == "properties" and isinstance(value, dict):
            converted[key] = {
                name: _to_gemini_schema(sub) if isinstance(sub, dict) else sub
                for name, sub in value.items()
            }
        elif key == "items" and isinstance(value, dict):
            converted[key] = _to_gemini_schema(value)
        else:
            converted[key] = value
    return converted


def _extract_text(data: dict) -> str:
    """Pull the reply text out of a generateContent response; empty if blocked/empty."""
    candidates = data.get("candidates") or []
    if not candidates:
        # No candidates => prompt blocked by safety filters or an empty result. Degrade to silence.
        logger.warning("Gemini returned no candidates (possibly blocked); delivering no reply.")
        return ""
    parts = ((candidates[0] or {}).get("content") or {}).get("parts") or []
    return "".join(str(part.get("text", "")) for part in parts).strip()


def _parse_json_object(raw: str, name: str) -> dict:
    """Parse a JSON-object env string to a dict; empty dict on blank/invalid (never raises)."""
    if not raw or not raw.strip():
        return {}
    try:
        data = json.loads(raw)
    except json.JSONDecodeError:
        logger.warning("%s is not valid JSON; ignoring it.", name)
        return {}
    if not isinstance(data, dict):
        logger.warning("%s must be a JSON object; ignoring it.", name)
        return {}
    return data
