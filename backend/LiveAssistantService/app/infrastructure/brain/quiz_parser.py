"""Shared parsing of the brain's strict-JSON quiz reply into a ``GeneratedQuiz``.

Used by every ``BrainClient`` so provider clients differ only in HOW they call the model, never in
how the reply is interpreted — the same split ``outcome_parser`` keeps.

The failure posture is the OPPOSITE of ``parse_outcome``. Evaluation degrades to silence because
nobody asked for it; a quiz was explicitly requested by a teacher standing in front of a class, so
an unusable reply returns ``None`` for the caller to report rather than a quietly empty quiz.

A response schema constrains shape, not sense: it cannot say "the index must be within the options
you generated", nor stop a model repeating the same option twice and making a question
unanswerable. Everything the schema cannot express is enforced here. Questions that break a rule
are dropped individually, so one bad question costs the teacher that question rather than the quiz.
"""

from __future__ import annotations

import json
import logging
from typing import Any

from app.domain.quiz.generated_quiz import GeneratedOption, GeneratedQuestion, GeneratedQuiz
from app.infrastructure.brain.outcome_parser import strip_code_fences

logger = logging.getLogger("liveassistant.brain")

DEFAULT_TITLE = "Quick check"


def parse_quiz(
    content: str,
    *,
    max_questions: int,
    min_options: int,
    max_options: int,
    citation_numbers: set[int],
    grounded: bool,
) -> GeneratedQuiz | None:
    """Parse and validate the model's reply. ``None`` when nothing usable survived."""
    try:
        data = json.loads(strip_code_fences(content))
    except (json.JSONDecodeError, TypeError):
        logger.warning("Brain returned non-JSON output for a quiz request.")
        return None

    if not isinstance(data, dict):
        logger.warning("Brain returned JSON that is not an object for a quiz request.")
        return None

    raw_questions = data.get("questions")
    if not isinstance(raw_questions, list):
        logger.warning("Brain returned a quiz with no questions array.")
        return None

    questions: list[GeneratedQuestion] = []
    for raw in raw_questions:
        question = _parse_question(raw, min_options=min_options, max_options=max_options)
        if question is not None:
            questions.append(question)
        if len(questions) >= max_questions:
            break  # honour the server's cap even if the model ignored it

    if not questions:
        logger.warning("Brain returned a quiz with no usable questions.")
        return None

    title = str(data.get("title") or "").strip() or DEFAULT_TITLE
    return GeneratedQuiz(
        title=title,
        questions=questions,
        citations=_valid_citations(data.get("citations"), citation_numbers),
        grounded=grounded,
    )


def parse_answers(
    content: str, question_text: str, *, min_options: int, max_options: int
) -> GeneratedQuestion | None:
    """Parse an options-only reply into the teacher's question. ``None`` if unusable.

    Routed through the SAME ``_parse_question`` as a full quiz, so the index-in-range and
    duplicate-option rules cannot drift between the two paths. The question text is the teacher's
    own and is never taken from the model — it was not asked for, and accepting one would let a
    request for answers quietly rewrite the question.
    """
    try:
        data = json.loads(strip_code_fences(content))
    except (json.JSONDecodeError, TypeError):
        logger.warning("Brain returned non-JSON output for an answers request.")
        return None

    if not isinstance(data, dict):
        logger.warning("Brain returned JSON that is not an object for an answers request.")
        return None

    return _parse_question(
        {**data, "text": question_text}, min_options=min_options, max_options=max_options
    )


def _parse_question(
    raw: Any, *, min_options: int, max_options: int
) -> GeneratedQuestion | None:
    """One question, or None if it breaks a rule the schema could not enforce."""
    if not isinstance(raw, dict):
        return None

    text = str(raw.get("text") or "").strip()
    if not text:
        return None

    raw_options = raw.get("options")
    if not isinstance(raw_options, list):
        return None

    # De-duplicated in first-seen order: a repeated option makes a question unanswerable, and
    # removing the duplicate is kinder than dropping the question when enough distinct ones remain.
    seen: list[str] = []
    for value in raw_options:
        option = str(value or "").strip()
        if option and option.casefold() not in {s.casefold() for s in seen}:
            seen.append(option)

    if len(seen) < min_options:
        return None
    options = seen[:max_options]

    correct_index = raw.get("correct_index")
    # bools are ints in Python; an index is never a bool.
    if isinstance(correct_index, bool) or not isinstance(correct_index, int):
        return None
    # The schema cannot tie the index to the array it indexes — truncation above can also have put
    # the answer out of range, which would silently mark the wrong option correct.
    if not 0 <= correct_index < len(options):
        return None

    return GeneratedQuestion(
        text=text,
        options=[
            GeneratedOption(text=option, is_correct=index == correct_index)
            for index, option in enumerate(options)
        ],
    )


def _valid_citations(raw: Any, citation_numbers: set[int]) -> list[int]:
    """Keep only in-range integer citations, de-duplicated in first-seen order."""
    if not isinstance(raw, list):
        return []
    kept: list[int] = []
    for value in raw:
        if isinstance(value, bool):
            continue
        if isinstance(value, int) and value in citation_numbers and value not in kept:
            kept.append(value)
    return kept
