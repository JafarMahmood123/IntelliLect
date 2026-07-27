"""BrainClient backed by a LOCAL generative model in host Ollama.

Calls ``POST /api/chat`` (stream:false) with this service's own grounded,
silence-biased evaluation prompt and parses the reply as STRICT JSON. No model
weights live in this container. Mirrors KnowledgeService's generation provider
(optional bearer auth, configurable timeout, clear errors).

Degradation policy: a transport/HTTP failure raises ``OllamaBrainError`` (the caller
degrades to "no feedback"); a model reply that is not valid JSON degrades to "no
feedback" HERE, so a chatty or malformed model can never crash the live loop.
"""

from __future__ import annotations

import json
import logging

import httpx

from app.application.ports.brain_client import BrainClient
from app.domain.evaluation.evaluation_outcome import EvaluationOutcome
from app.domain.evaluation.feedback_type import FeedbackType
from app.domain.evaluation.retrieved_chunk import RetrievedChunk
from app.domain.evaluation.teacher_suggestion import TeacherSuggestion
from app.domain.idea.completed_idea import CompletedIdea
from app.infrastructure.brain.evaluation_prompt import (
    SYSTEM_PROMPT,
    build_numbered_context,
    build_user_prompt,
)
from app.infrastructure.config.settings import Settings

logger = logging.getLogger("liveassistant.brain")

_TYPE_BY_NAME = {
    "discrepancy": FeedbackType.DISCREPANCY,
    "gap": FeedbackType.GAP,
    "unclear": FeedbackType.UNCLEAR,
    "none": FeedbackType.NONE,
}


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
        self._headers: dict[str, str] = {}
        if settings.ollama_auth_token:
            self._headers["Authorization"] = f"Bearer {settings.ollama_auth_token}"

    async def evaluate(
        self, idea: CompletedIdea, chunks: list[RetrievedChunk]
    ) -> EvaluationOutcome:
        context, citation_map = build_numbered_context(chunks)
        content = await self._complete(SYSTEM_PROMPT, build_user_prompt(idea.text, context))
        return self._parse_outcome(content, citation_map)

    async def smoke_complete(self, transcript_text: str) -> str:
        """SMOKE-TEST ONLY (temporary): raw transcript -> LLM -> raw reply.

        A plain, friendly assistant turn (NOT the grounded evaluation prompt) so we can eyeball
        that the model is reachable and responding. Remove with the smoke branch once verified.
        """
        system = (
            "You are a helpful teaching assistant listening to a live lecture. "
            "The teacher just said something. Reply with ONE short, helpful remark "
            "or clarification (at most two sentences)."
        )
        return await self._complete(system, transcript_text)

    async def _complete(self, system: str, user: str) -> str:
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
            "options": {"temperature": self._temperature, "num_predict": self._max_tokens},
        }
        # Bound generation to a core budget so it doesn't fight STT for all 8 cores
        # (0 => omit and let Ollama use its default = all cores). See settings.eval_num_thread.
        if self._num_thread:
            payload["options"]["num_thread"] = self._num_thread
        try:
            async with httpx.AsyncClient(
                timeout=self._timeout, headers=self._headers
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

    def _parse_outcome(
        self, content: str, citation_map: dict[int, RetrievedChunk]
    ) -> EvaluationOutcome:
        """Parse the model's strict-JSON reply; degrade to no-feedback on any issue."""
        try:
            data = json.loads(_strip_code_fences(content))
        except (json.JSONDecodeError, TypeError):
            logger.warning("Brain returned non-JSON output; degrading to no feedback.")
            return EvaluationOutcome.none()

        if not isinstance(data, dict) or not data.get("has_feedback"):
            return EvaluationOutcome.none()

        suggestion_text = str(data.get("suggestion") or "").strip()
        feedback_type = _TYPE_BY_NAME.get(str(data.get("type", "")).strip().lower())
        # Bias to silence: need real text and a concrete problem type to speak up.
        if not suggestion_text or feedback_type is None or feedback_type is FeedbackType.NONE:
            return EvaluationOutcome.none()

        citations = _valid_citations(data.get("citations"), citation_map)
        sources = [citation_map[n] for n in citations]
        return EvaluationOutcome(
            has_feedback=True,
            suggestion=TeacherSuggestion(
                text=suggestion_text,
                type=feedback_type,
                citations=citations,
                sources=sources,
                confidence=_parse_confidence(data.get("confidence"), self._default_confidence),
            ),
        )


def _strip_code_fences(content: str) -> str:
    """Remove a leading/trailing ``` or ```json fence if the model wrapped its JSON."""
    text = (content or "").strip()
    if not text.startswith("```"):
        return text
    lines = text.splitlines()
    # Drop the opening fence line (``` or ```json) and a closing fence if present.
    lines = lines[1:]
    if lines and lines[-1].strip().startswith("```"):
        lines = lines[:-1]
    return "\n".join(lines).strip()


def _parse_confidence(raw, default: float) -> float:
    """A number in [0, 1] from the model, clamped; ``default`` if absent/invalid.

    Booleans are rejected (a confidence is never a bool) — kept backward-compatible so
    a model that omits the field never crashes parsing.
    """
    if isinstance(raw, bool) or not isinstance(raw, (int, float)):
        return default
    return max(0.0, min(1.0, float(raw)))


def _valid_citations(raw, citation_map: dict[int, RetrievedChunk]) -> list[int]:
    """Keep only in-range integer citations, de-duplicated in first-seen order."""
    if not isinstance(raw, list):
        return []
    seen: list[int] = []
    for value in raw:
        if isinstance(value, bool):
            continue  # bools are ints in Python; a citation is never a bool
        if isinstance(value, int) and value in citation_map and value not in seen:
            seen.append(value)
    return seen
