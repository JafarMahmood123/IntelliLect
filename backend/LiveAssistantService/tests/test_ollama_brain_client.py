"""OFFLINE tests for OllamaBrainClient parsing + citation mapping.

The HTTP call (`_complete`) is overridden with canned content, so no Ollama is
needed. Covers strict-JSON parsing, code-fence stripping, malformed-output fallback,
silence bias, and citation -> source mapping.
"""

from __future__ import annotations

from uuid import uuid4

from app.domain.evaluation.feedback_type import FeedbackType
from app.domain.evaluation.retrieved_chunk import RetrievedChunk
from app.domain.idea.boundary_trigger import BoundaryTrigger
from app.domain.idea.completed_idea import CompletedIdea
from app.infrastructure.brain.ollama_brain_client import OllamaBrainClient
from app.infrastructure.config.settings import Settings


class _StubBrain(OllamaBrainClient):
    """OllamaBrainClient whose model reply is fixed (no HTTP)."""

    def __init__(self, content: str) -> None:
        super().__init__(Settings())
        self._content = content

    async def _complete(self, system: str, user: str) -> str:
        return self._content


def _chunks(n: int = 3) -> list[RetrievedChunk]:
    return [
        RetrievedChunk(f"chunk {i}", 0.9 - i * 0.1, uuid4(), uuid4(), slide=i + 1)
        for i in range(n)
    ]


def _idea() -> CompletedIdea:
    return CompletedIdea("teacher explanation", 0, 1000, 1, BoundaryTrigger.PAUSE)


async def test_valid_json_maps_citations_to_sources():
    chunks = _chunks(3)
    content = (
        '{"has_feedback": true, "type": "discrepancy", '
        '"suggestion": "Reconsider the location [1] and add [3].", "citations": [1, 3]}'
    )
    outcome = await _StubBrain(content).evaluate(_idea(), chunks)

    assert outcome.has_feedback is True
    assert outcome.suggestion.type is FeedbackType.DISCREPANCY
    assert outcome.suggestion.citations == [1, 3]
    assert outcome.suggestion.sources == [chunks[0], chunks[2]]  # 1-based mapping


async def test_confidence_is_parsed_when_present():
    content = ('{"has_feedback": true, "type": "gap", "suggestion": "Add [1].", '
               '"citations": [1], "confidence": 0.42}')
    outcome = await _StubBrain(content).evaluate(_idea(), _chunks(1))

    assert outcome.suggestion.confidence == 0.42


async def test_missing_confidence_uses_configured_default():
    content = '{"has_feedback": true, "type": "gap", "suggestion": "Add [1].", "citations": [1]}'
    outcome = await _StubBrain(content).evaluate(_idea(), _chunks(1))

    # Settings() default feedback_default_confidence is 0.6.
    assert outcome.suggestion.confidence == Settings().feedback_default_confidence


async def test_out_of_range_or_invalid_confidence_is_clamped_or_defaulted():
    high = await _StubBrain('{"has_feedback": true, "type": "gap", "suggestion": "x [1].", "citations": [1], "confidence": 1.5}').evaluate(_idea(), _chunks(1))
    low = await _StubBrain('{"has_feedback": true, "type": "gap", "suggestion": "x [1].", "citations": [1], "confidence": -0.3}').evaluate(_idea(), _chunks(1))
    bad = await _StubBrain('{"has_feedback": true, "type": "gap", "suggestion": "x [1].", "citations": [1], "confidence": "high"}').evaluate(_idea(), _chunks(1))

    assert high.suggestion.confidence == 1.0
    assert low.suggestion.confidence == 0.0
    assert bad.suggestion.confidence == Settings().feedback_default_confidence


async def test_code_fenced_json_is_parsed():
    content = '```json\n{"has_feedback": true, "type": "gap", "suggestion": "Add [1].", "citations": [1]}\n```'
    outcome = await _StubBrain(content).evaluate(_idea(), _chunks(1))

    assert outcome.has_feedback is True
    assert outcome.suggestion.type is FeedbackType.GAP
    assert outcome.suggestion.sources[0].slide == 1


async def test_malformed_json_falls_back_to_no_feedback():
    outcome = await _StubBrain("I think the teacher is basically right.").evaluate(_idea(), _chunks())

    assert outcome.has_feedback is False
    assert outcome.suggestion is None


async def test_has_feedback_false_is_no_feedback():
    content = '{"has_feedback": false, "type": "none", "suggestion": "", "citations": []}'
    outcome = await _StubBrain(content).evaluate(_idea(), _chunks())

    assert outcome.has_feedback is False


async def test_feedback_true_but_empty_suggestion_biases_to_silence():
    content = '{"has_feedback": true, "type": "gap", "suggestion": "   ", "citations": [1]}'
    outcome = await _StubBrain(content).evaluate(_idea(), _chunks())

    assert outcome.has_feedback is False  # no real text -> stay silent


async def test_type_none_with_text_is_no_feedback():
    content = '{"has_feedback": true, "type": "none", "suggestion": "something", "citations": []}'
    outcome = await _StubBrain(content).evaluate(_idea(), _chunks())

    assert outcome.has_feedback is False


async def test_out_of_range_and_bogus_citations_are_dropped():
    chunks = _chunks(3)  # valid citation numbers are 1..3
    content = (
        '{"has_feedback": true, "type": "unclear", "suggestion": "See [2].", '
        '"citations": [2, 99, 0, 2, "x", true]}'
    )
    outcome = await _StubBrain(content).evaluate(_idea(), chunks)

    assert outcome.suggestion.citations == [2]  # deduped, in-range, ints only
    assert outcome.suggestion.sources == [chunks[1]]
