"""The response schema — the layer that constrains the model during decoding.

These are cheap tests for an expensive lesson: the first version left ``minItems`` at 1, and a
teacher who asked for three questions got one, because nothing in the schema required more and the
prompt gave the model permission to write fewer.
"""

from __future__ import annotations

from app.infrastructure.brain.gemini_brain_client import _to_gemini_schema
from app.infrastructure.brain.quiz_prompt import (
    build_answers_response_schema,
    build_response_schema,
    build_user_prompt,
)


def test_the_requested_number_of_questions_is_required_not_merely_allowed():
    questions = build_response_schema(max_questions=3, min_options=2, max_options=4)[
        "properties"
    ]["questions"]

    assert questions["minItems"] == 3
    assert questions["maxItems"] == 3


def test_option_bounds_come_from_the_caller():
    options = build_response_schema(max_questions=1, min_options=3, max_options=5)[
        "properties"
    ]["questions"]["items"]["properties"]["options"]

    assert options["minItems"] == 3
    assert options["maxItems"] == 5


def test_a_question_names_its_answer_by_index_not_by_boolean():
    """The device that makes "exactly one correct answer" unbreakable: a single integer cannot
    express "all correct" or "none correct", which per-option booleans can."""
    question = build_response_schema(max_questions=1, min_options=2, max_options=4)[
        "properties"
    ]["questions"]["items"]

    assert question["properties"]["correct_index"]["type"] == "integer"
    assert question["properties"]["options"]["items"] == {"type": "string"}
    assert "correct_index" in question["required"]


def test_answers_schema_uses_the_same_index_device():
    schema = build_answers_response_schema(min_options=2, max_options=4)

    assert schema["properties"]["correct_index"]["type"] == "integer"
    assert schema["required"] == ["options", "correct_index"]


def test_the_prompt_asks_for_an_exact_count():
    prompt = build_user_prompt("an idea", "[1]: material", 3)

    assert "EXACTLY 3" in prompt
    assert "at most" not in prompt


def test_already_written_questions_are_listed_for_the_model_to_avoid():
    prompt = build_user_prompt("an idea", "[1]: material", 1, ["What is a cache hit?"])

    assert "What is a cache hit?" in prompt


def test_no_avoid_block_when_the_composer_is_empty():
    assert "already written" not in build_user_prompt("an idea", "[1]: material", 1, [])


def test_gemini_dialect_uppercases_every_nested_type():
    """Gemini's Schema.type is a proto enum, so JSON must carry the enum NAME. A lowercase type
    anywhere in the tree is a 400 at request time, which surfaces as a failed generation."""
    converted = _to_gemini_schema(
        build_response_schema(max_questions=2, min_options=2, max_options=4)
    )

    def types(node):
        if isinstance(node, dict):
            for key, value in node.items():
                if key == "type" and isinstance(value, str):
                    yield value
                else:
                    yield from types(value)
        elif isinstance(node, list):
            for item in node:
                yield from types(item)

    found = list(types(converted))
    assert found  # guard against the walker silently finding nothing
    assert all(name.isupper() for name in found)


# --- correcting the teacher ----------------------------------------------------


def test_both_schemas_can_carry_corrections():
    """The model has to be able to SAY it disagreed. Without a field for it, the only ways to
    handle a teacher's mistake are to quiz the mistake or to fix it silently — and the silent fix
    is the worse of the two, because the teacher publishes an answer key contradicting what they
    just told the room and never finds out."""
    quiz = build_response_schema(max_questions=3, min_options=2, max_options=4)
    answers = build_answers_response_schema(min_options=2, max_options=4)

    for schema in (quiz, answers):
        corrections = schema["properties"]["corrections"]
        assert corrections["type"] == "array"
        assert set(corrections["items"]["required"]) == {"taught", "corrected"}


def test_a_correction_is_never_required():
    """Agreeing with the teacher is the normal case. A required field would push the model to
    manufacture a disagreement to fill it."""
    schema = build_response_schema(max_questions=3, min_options=2, max_options=4)

    assert "corrections" not in schema["required"]


def test_corrections_survive_translation_to_geminis_dialect():
    schema = _to_gemini_schema(
        build_response_schema(max_questions=3, min_options=2, max_options=4)
    )

    corrections = schema["properties"]["corrections"]
    assert corrections["type"] == "ARRAY"
    assert corrections["items"]["type"] == "OBJECT"
