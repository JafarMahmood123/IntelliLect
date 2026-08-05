"""Shared parsing of the brain's strict-JSON evaluation reply into an ``EvaluationOutcome``.

Used by every ``BrainClient`` (Ollama, Gemini, …) so provider clients differ only in HOW they call
the model, never in how the reply is interpreted. Any parse/validation failure degrades to
"no feedback" rather than raising, so a chatty or malformed model can never crash the live loop.
"""

from __future__ import annotations

import json
import logging
import re
import unicodedata

from app.domain.evaluation.evaluation_outcome import EvaluationOutcome
from app.domain.evaluation.feedback_type import FeedbackType
from app.domain.evaluation.retrieved_chunk import RetrievedChunk
from app.domain.evaluation.teacher_suggestion import TeacherSuggestion

logger = logging.getLogger("liveassistant.brain")

TYPE_BY_NAME = {
    "discrepancy": FeedbackType.DISCREPANCY,
    "gap": FeedbackType.GAP,
    "likely": FeedbackType.LIKELY,
    # "unclear" was this category's name until the rename. Kept as an alias because the word is
    # the obvious synonym for a model to reach for, and silently dropping otherwise-good feedback
    # over one word would be the worst possible reading of a strict parser.
    "unclear": FeedbackType.LIKELY,
    "none": FeedbackType.NONE,
}

# The longest quote worth highlighting. Past this the model has stopped pointing at a wrong word
# and started quoting the paragraph back, which highlights everything and locates nothing.
MAX_SPAN_CHARS = 160


def parse_outcome(
    content: str,
    citation_map: dict[int, RetrievedChunk],
    default_confidence: float,
    idea_text: str = "",
) -> EvaluationOutcome:
    """Parse the model's strict-JSON reply; degrade to no-feedback on any issue.

    ``idea_text`` is what the teacher actually said, and it is the only thing that can prove a
    quoted span is real. Without it the spans are dropped rather than trusted — see
    :func:`verified_span`.
    """
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
    incorrect_text, corrected_text = parse_span(data, idea_text)
    return EvaluationOutcome(
        has_feedback=True,
        suggestion=TeacherSuggestion(
            text=suggestion_text,
            type=feedback_type,
            citations=citations,
            sources=sources,
            confidence=parse_confidence(data.get("confidence"), default_confidence),
            incorrect_text=incorrect_text,
            corrected_text=corrected_text,
        ),
    )


def parse_span(data: dict, idea_text: str) -> tuple[str | None, str | None]:
    """The verified (incorrect, corrected) pair, or ``(None, None)``.

    A correction is only meaningful next to the thing it corrects, so a ``corrected_text`` whose
    ``incorrect_text`` did not survive verification is dropped with it. The suggestion paragraph
    is untouched either way — losing the highlight never costs the teacher the feedback itself.
    """
    incorrect = verified_span(data.get("incorrect_text"), idea_text)
    if incorrect is None:
        return None, None
    return incorrect, clean_span(data.get("corrected_text"))


def verified_span(raw, idea_text: str) -> str | None:
    """A quote confirmed to appear in what the teacher said, else ``None``.

    The brain is asked for a verbatim quote, and mostly obliges — but "mostly" is not good enough
    when the payoff is painting words red in front of a teacher mid-lecture. A hallucinated or
    helpfully-tidied quote highlights something that was never said, which reads as the assistant
    mishearing the lecture and costs it the teacher's trust for the rest of the session.

    Matching is deliberately loose about the things speech-to-text and models disagree on —
    case, punctuation, whitespace runs, unicode quote and dash variants — and strict about the
    words themselves. Nothing weaker than "these exact words, in this order" counts.
    """
    span = clean_span(raw)
    if span is None or not idea_text:
        return None
    if normalized(span) not in normalized(idea_text):
        logger.info("brain_span_rejected", extra={"span_chars": len(span)})
        return None
    return span


def clean_span(raw) -> str | None:
    """A usable span string, or ``None`` for absent/blank/oversized values."""
    if not isinstance(raw, str):
        return None
    span = raw.strip()
    if not span or len(span) > MAX_SPAN_CHARS:
        return None
    return span


def normalized(text: str) -> str:
    """Casefolded, punctuation-stripped, single-spaced form used only for span comparison."""
    # NFKD folds the typographic variants (curly quotes, en/em dashes, ligatures) that a model
    # emits and a transcript does not, so they cannot cause a spurious mismatch. Stripping
    # non-word characters then takes the combining accents with them, which is the same
    # forgiveness applied to "café" vs "cafe".
    folded = unicodedata.normalize("NFKD", text).casefold()
    return re.sub(r"\s+", " ", re.sub(r"[^\w\s]", "", folded)).strip()




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
