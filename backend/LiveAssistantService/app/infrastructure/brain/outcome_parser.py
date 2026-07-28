"""Shared parsing of the brain's strict-JSON evaluation reply into an ``EvaluationOutcome``.

Used by every ``BrainClient`` (Ollama, Gemini, …) so provider clients differ only in HOW they call
the model, never in how the reply is interpreted. Any parse/validation failure degrades to
"no feedback" rather than raising, so a chatty or malformed model can never crash the live loop.
"""

from __future__ import annotations

import json
import logging

from app.domain.evaluation.evaluation_outcome import EvaluationOutcome
from app.domain.evaluation.feedback_type import FeedbackType
from app.domain.evaluation.retrieved_chunk import RetrievedChunk
from app.domain.evaluation.teacher_suggestion import TeacherSuggestion

logger = logging.getLogger("liveassistant.brain")

TYPE_BY_NAME = {
    "discrepancy": FeedbackType.DISCREPANCY,
    "gap": FeedbackType.GAP,
    "unclear": FeedbackType.UNCLEAR,
    "none": FeedbackType.NONE,
}


def parse_outcome(
    content: str,
    citation_map: dict[int, RetrievedChunk],
    default_confidence: float,
) -> EvaluationOutcome:
    """Parse the model's strict-JSON reply; degrade to no-feedback on any issue."""
    try:
        data = json.loads(strip_code_fences(content))
    except (json.JSONDecodeError, TypeError):
        logger.warning("Brain returned non-JSON output; degrading to no feedback.")
        return EvaluationOutcome.none()

    if not isinstance(data, dict) or not data.get("has_feedback"):
        return EvaluationOutcome.none()

    suggestion_text = str(data.get("suggestion") or "").strip()
    feedback_type = TYPE_BY_NAME.get(str(data.get("type", "")).strip().lower())
    # Bias to silence: need real text and a concrete problem type to speak up.
    if not suggestion_text or feedback_type is None or feedback_type is FeedbackType.NONE:
        return EvaluationOutcome.none()

    citations = valid_citations(data.get("citations"), citation_map)
    sources = [citation_map[n] for n in citations]
    return EvaluationOutcome(
        has_feedback=True,
        suggestion=TeacherSuggestion(
            text=suggestion_text,
            type=feedback_type,
            citations=citations,
            sources=sources,
            confidence=parse_confidence(data.get("confidence"), default_confidence),
        ),
    )


def strip_code_fences(content: str) -> str:
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


def parse_confidence(raw, default: float) -> float:
    """A number in [0, 1] from the model, clamped; ``default`` if absent/invalid.

    Booleans are rejected (a confidence is never a bool) — kept backward-compatible so a model that
    omits the field never crashes parsing.
    """
    if isinstance(raw, bool) or not isinstance(raw, (int, float)):
        return default
    return max(0.0, min(1.0, float(raw)))


def valid_citations(raw, citation_map: dict[int, RetrievedChunk]) -> list[int]:
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
