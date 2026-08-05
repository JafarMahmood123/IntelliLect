"""OFFLINE tests for the correction span: the (incorrect_text, corrected_text) pair.

The span is what makes a coloured highlight possible — red on the words that were wrong, green on
what they should have been. It is also the one field the brain can get dangerously wrong: a quote
that was never spoken, painted red in front of a class, reads as the assistant mishearing the
lecture. So the parser verifies every quote against the teacher's actual words, and these tests
are mostly about what it REFUSES.

Pure parsing — no HTTP, no model.
"""

from __future__ import annotations

import json

from app.domain.evaluation.feedback_severity import FeedbackSeverity, severity_of
from app.domain.evaluation.feedback_type import FeedbackType
from app.infrastructure.brain.outcome_parser import MAX_SPAN_CHARS, parse_outcome

IDEA = "The treaty was signed in 1919, and the conference opened in Paris that January."


def _reply(**overrides) -> str:
    body = {
        "has_feedback": True,
        "type": "discrepancy",
        "suggestion": "Check the date against [1].",
        "citations": [],
        "confidence": 0.9,
    }
    body.update(overrides)
    return json.dumps(body)


def _parse(content: str, idea: str = IDEA):
    return parse_outcome(content, {}, 0.6, idea)


def test_verbatim_span_survives_with_its_correction():
    outcome = _parse(_reply(incorrect_text="signed in 1919", corrected_text="signed in 1920"))

    assert outcome.suggestion.incorrect_text == "signed in 1919"
    assert outcome.suggestion.corrected_text == "signed in 1920"


def test_span_the_teacher_never_said_is_dropped():
    # The suggestion itself survives — losing the highlight must never cost the teacher the
    # feedback. Only the unverifiable part goes.
    outcome = _parse(_reply(incorrect_text="signed in 1815", corrected_text="signed in 1919"))

    assert outcome.has_feedback is True
    assert outcome.suggestion.text == "Check the date against [1]."
    assert outcome.suggestion.incorrect_text is None
    assert outcome.suggestion.corrected_text is None


def test_a_correction_with_nothing_to_correct_is_dropped():
    # Green text alone says "it should be X" without ever showing what X replaces.
    outcome = _parse(_reply(corrected_text="signed in 1920"))

    assert outcome.suggestion.corrected_text is None


def test_a_verified_span_may_stand_without_a_correction():
    # Knowing a claim is wrong does not require knowing the right answer.
    outcome = _parse(_reply(incorrect_text="signed in 1919"))

    assert outcome.suggestion.incorrect_text == "signed in 1919"
    assert outcome.suggestion.corrected_text is None


def test_matching_forgives_case_punctuation_and_spacing():
    # What a model returns and what speech-to-text produced rarely agree on these, and none of
    # them change which words were said.
    outcome = _parse(_reply(incorrect_text="Signed  In 1919,"))

    assert outcome.suggestion.incorrect_text == "Signed  In 1919,"


def test_matching_forgives_typographic_variants():
    idea = "He called it a first-class result — the best so far."
    outcome = _parse(_reply(incorrect_text="first–class result"), idea)

    assert outcome.suggestion.incorrect_text == "first–class result"


def test_reordered_words_do_not_match():
    # Loose about punctuation, strict about the words themselves and their order.
    outcome = _parse(_reply(incorrect_text="1919 in signed"))

    assert outcome.suggestion.incorrect_text is None


def test_span_longer_than_the_cap_is_dropped():
    long_quote = "x" * (MAX_SPAN_CHARS + 1)
    outcome = _parse(_reply(incorrect_text=long_quote), long_quote)

    # It IS in the idea text, so this is the cap talking: quoting the whole paragraph back
    # highlights everything and locates nothing.
    assert outcome.suggestion.incorrect_text is None


def test_blank_and_non_string_spans_are_ignored():
    for value in ("", "   ", 1919, None, ["signed in 1919"]):
        outcome = _parse(_reply(incorrect_text=value))
        assert outcome.suggestion.incorrect_text is None, value


def test_spans_are_dropped_when_the_idea_text_is_unavailable():
    # Nothing to verify against means nothing can be trusted.
    outcome = _parse(_reply(incorrect_text="signed in 1919"), "")

    assert outcome.suggestion.incorrect_text is None


def test_omitting_the_span_entirely_is_the_ordinary_case():
    outcome = _parse(_reply(type="gap", suggestion="You did not mention the reparations [1]."))

    assert outcome.has_feedback is True
    assert outcome.suggestion.incorrect_text is None
    assert outcome.suggestion.corrected_text is None


def test_likely_is_parsed_and_unclear_still_maps_to_it():
    # "unclear" was this category's name before the rename; a model reaching for the old synonym
    # must not lose its feedback over one word.
    assert _parse(_reply(type="likely")).suggestion.type is FeedbackType.LIKELY
    assert _parse(_reply(type="unclear")).suggestion.type is FeedbackType.LIKELY


def test_severity_is_derived_from_the_feedback_type():
    assert severity_of(FeedbackType.DISCREPANCY) is FeedbackSeverity.INCORRECT
    assert severity_of(FeedbackType.LIKELY) is FeedbackSeverity.LIKELY
    assert severity_of(FeedbackType.GAP) is FeedbackSeverity.MISSING
    # NONE never reaches a client; it must still not raise on a display concern.
    assert severity_of(FeedbackType.NONE) is FeedbackSeverity.MISSING
