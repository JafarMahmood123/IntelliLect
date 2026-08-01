"""Everything the response schema CANNOT enforce.

A schema constrains shape: field names, types, array bounds. It cannot say "that index points at an
option you actually generated", nor stop a model offering the same answer twice. Those rules live in
the parser, and these are the tests that hold them — a quiz that breaks one is either unanswerable
or marks the wrong option correct, and both reach students silently.
"""

from __future__ import annotations

import json

from app.infrastructure.brain.quiz_parser import DEFAULT_TITLE, parse_quiz

BOUNDS = {"max_questions": 5, "min_options": 2, "max_options": 4}


def _reply(**overrides) -> str:
    question = {
        "text": "What is a cache miss?",
        "options": ["Item not in the cache", "Item in the cache"],
        "correct_index": 0,
    }
    question.update(overrides.pop("question", {}))
    body = {"title": "Caching", "questions": [question], "citations": [1]}
    body.update(overrides)
    return json.dumps(body)


def _parse(content: str, *, citations: set[int] | None = None, grounded: bool = True):
    return parse_quiz(
        content,
        citation_numbers=citations if citations is not None else {1, 2},
        grounded=grounded,
        **BOUNDS,
    )


def test_parses_a_well_formed_reply():
    quiz = _parse(_reply())

    assert quiz is not None
    assert quiz.title == "Caching"
    assert len(quiz.questions) == 1
    assert [o.is_correct for o in quiz.questions[0].options] == [True, False]


def test_correct_index_marks_exactly_one_option():
    """The reason the schema uses an index instead of a boolean per option.

    A model cannot express "all correct" or "none correct" through an index, so the rule grading
    depends on holds structurally rather than by validation.
    """
    quiz = _parse(_reply(question={"options": ["a", "b", "c"], "correct_index": 2}))

    assert quiz is not None
    assert [o.is_correct for o in quiz.questions[0].options] == [False, False, True]


def test_index_outside_the_options_drops_the_question():
    """Otherwise nothing would be marked correct and the question could never be scored."""
    assert _parse(_reply(question={"options": ["a", "b"], "correct_index": 7})) is None


def test_negative_index_drops_the_question():
    assert _parse(_reply(question={"options": ["a", "b"], "correct_index": -1})) is None


def test_boolean_index_is_rejected():
    """bools are ints in Python, so `True` would otherwise pass an isinstance check and silently
    mark option 1 correct."""
    assert _parse(_reply(question={"options": ["a", "b"], "correct_index": True})) is None


def test_duplicate_options_are_removed():
    quiz = _parse(
        _reply(question={"options": ["Same", "same", "Different"], "correct_index": 0})
    )

    assert quiz is not None
    assert [o.text for o in quiz.questions[0].options] == ["Same", "Different"]


def test_question_dropped_when_dedup_leaves_too_few_options():
    """Two identical options are one real choice; asking it would be meaningless."""
    assert _parse(_reply(question={"options": ["Same", "same"], "correct_index": 0})) is None


def test_truncation_that_would_orphan_the_answer_drops_the_question():
    """max_options truncates the list; if the correct option was beyond the cut, keeping the
    question would silently mark a wrong option correct."""
    result = _parse(
        _reply(question={"options": ["a", "b", "c", "d", "e"], "correct_index": 4})
    )

    assert result is None


def test_extra_questions_are_capped_to_the_limit():
    questions = [
        {"text": f"Q{i}", "options": ["a", "b"], "correct_index": 0} for i in range(9)
    ]
    quiz = _parse(json.dumps({"title": "Many", "questions": questions}))

    assert quiz is not None
    assert len(quiz.questions) == BOUNDS["max_questions"]


def test_one_bad_question_does_not_lose_the_others():
    questions = [
        {"text": "Good", "options": ["a", "b"], "correct_index": 0},
        {"text": "Bad", "options": ["a", "b"], "correct_index": 9},
        {"text": "Also good", "options": ["c", "d"], "correct_index": 1},
    ]
    quiz = _parse(json.dumps({"title": "Mixed", "questions": questions}))

    assert quiz is not None
    assert [q.text for q in quiz.questions] == ["Good", "Also good"]


def test_blank_question_text_is_dropped():
    assert _parse(_reply(question={"text": "   "})) is None


def test_non_json_returns_none_rather_than_an_empty_quiz():
    """Unlike evaluation, silence is not a valid outcome here — a teacher asked for this."""
    assert _parse("I'd be happy to help! Here are some questions...") is None


def test_json_that_is_not_an_object_returns_none():
    assert _parse("[1, 2, 3]") is None


def test_missing_questions_array_returns_none():
    assert _parse(json.dumps({"title": "Empty"})) is None


def test_code_fenced_json_is_still_parsed():
    assert _parse(f"```json\n{_reply()}\n```") is not None


def test_missing_title_falls_back_to_a_default():
    quiz = _parse(json.dumps({"questions": [
        {"text": "Q", "options": ["a", "b"], "correct_index": 0}
    ]}))

    assert quiz is not None
    assert quiz.title == DEFAULT_TITLE


def test_citations_outside_the_retrieved_range_are_dropped():
    """A citation the teacher cannot follow back to a source is worse than none."""
    quiz = _parse(_reply(citations=[1, 99, 2, 2]), citations={1, 2})

    assert quiz is not None
    assert quiz.citations == [1, 2]


def test_grounded_flag_is_carried_through():
    quiz = _parse(_reply(), grounded=False)

    assert quiz is not None
    assert quiz.grounded is False
