"""The quiz-generation prompt and its enforced response schema.

Kept in code beside ``evaluation_prompt`` and built the same way: grounded (use the teacher's words
and the numbered course material, never outside knowledge) and strict JSON out.

The schema is the important part. It is handed to the provider as a RESPONSE SCHEMA — Gemini's
``responseSchema`` and Ollama's ``format`` both constrain decoding against it — so a malformed
shape is prevented at generation time rather than caught afterwards. The parser still validates,
because a schema constrains shape and cannot constrain sense.

One choice does more work than the rest: a question names its answer with ``correct_index``, an
integer, instead of giving every option an ``is_correct`` boolean. "Exactly one option is correct"
is a rule JSON Schema cannot express over booleans — a model could return all true, or none — but a
single index makes it structurally impossible to say anything else. It converts the rule that
matters most into one the schema enforces for free.
"""

from __future__ import annotations

from typing import Any

SYSTEM_PROMPT = (
    "You are a teaching assistant helping a teacher quiz their class during a live lesson.\n\n"
    "You are given the explanation the teacher has JUST finished giving, and NUMBERED reference "
    "material from THIS course.\n\n"
    "Write multiple-choice questions that check whether students understood THAT explanation.\n\n"
    "Rules:\n"
    "- Question ONLY what the teacher actually explained. The reference material is there to keep "
    "you accurate and to settle wording — it is NOT a second syllabus to quiz from.\n"
    "- Never invent facts or use outside knowledge.\n"
    "- Write EXACTLY the number of questions asked for. If the explanation is short, test "
    "different aspects of it — a definition, a consequence, a comparison, an edge case — rather "
    "than asking the same thing twice in different words.\n"
    "- Exactly one option per question is correct.\n"
    "- The wrong options must be plausible: a believable misunderstanding of THIS explanation, "
    "not obvious filler. A question every student answers correctly teaches the teacher nothing.\n"
    "- Keep questions and options short and plain; they are read on a phone mid-lesson.\n"
    "- Do not number the questions, and do not prefix options with A/B/C.\n"
    "- Cite by [n] the material you relied on, in the citations field.\n"
    "- Write in the same language the teacher was speaking."
)


def build_response_schema(
    *, max_questions: int, min_options: int, max_options: int
) -> dict[str, Any]:
    """The JSON schema the provider must generate against.

    The bounds come from the server's own quiz limits, so the model cannot propose a quiz the
    configured limits would reject.
    """
    return {
        "type": "object",
        "properties": {
            "title": {
                "type": "string",
                "description": "A short title naming the idea being tested.",
            },
            "questions": {
                "type": "array",
                # minItems EQUALS maxItems on purpose. With a floor of 1 the model would satisfy
                # the schema by writing a single question and, given any excuse in the prompt to
                # write fewer, it did exactly that. A teacher who asks for three wants three;
                # constrained decoding is what actually delivers them.
                "minItems": max_questions,
                "maxItems": max_questions,
                "items": {
                    "type": "object",
                    "properties": {
                        "text": {"type": "string", "description": "The question."},
                        "options": {
                            "type": "array",
                            "minItems": min_options,
                            "maxItems": max_options,
                            "items": {"type": "string"},
                        },
                        "correct_index": {
                            "type": "integer",
                            "description": "0-based index into options of the ONE correct answer.",
                        },
                    },
                    "required": ["text", "options", "correct_index"],
                },
            },
            "citations": {
                "type": "array",
                "items": {"type": "integer"},
                "description": "Numbers [n] of the reference material used.",
            },
        },
        "required": ["title", "questions"],
    }


def _material_block(context: str) -> str:
    return context.strip() or (
        "(none found for this explanation — rely only on the teacher's words above, "
        "and leave citations empty)"
    )


def _avoid_block(avoid: list[str] | None) -> str:
    """Questions already in the teacher's draft, so a new one does not repeat them."""
    if not avoid:
        return ""
    listed = "\n".join(f"- {text}" for text in avoid if text.strip())
    if not listed:
        return ""
    return (
        "\n\nThe teacher has already written these questions. Ask about something they do NOT "
        f"cover:\n{listed}"
    )


def build_user_prompt(
    idea_text: str, context: str, question_count: int, avoid: list[str] | None = None
) -> str:
    """The turn: what was just taught, what the course says, and how many questions to write."""
    plural = "question" if question_count == 1 else "questions"
    return (
        f"The teacher has just finished explaining:\n{idea_text}\n\n"
        f"Reference material from this course:\n{_material_block(context)}"
        f"{_avoid_block(avoid)}\n\n"
        f"Write EXACTLY {question_count} multiple-choice {plural} checking whether students "
        "understood that explanation. Respond with ONLY the JSON."
    )


# --- answers for a question the teacher wrote themselves ----------------------

ANSWERS_SYSTEM_PROMPT = (
    "You are a teaching assistant helping a teacher finish a multiple-choice question during a "
    "live lesson.\n\n"
    "The teacher has written the QUESTION themselves. You are given that question, the "
    "explanation they just gave in class, and NUMBERED reference material from THIS course. "
    "Write the answer options.\n\n"
    "Rules:\n"
    "- Answer the teacher's question as asked. Do not reinterpret or rewrite it.\n"
    "- Exactly one option is correct, and it must be correct according to the explanation and the "
    "material — never according to outside knowledge.\n"
    "- The wrong options must be plausible: a believable misunderstanding of THIS explanation, "
    "not obvious filler. A question every student answers correctly teaches the teacher nothing.\n"
    "- Keep the options short and plain; they are read on a phone mid-lesson.\n"
    "- Do not prefix options with A/B/C or numbers.\n"
    "- Write in the same language as the question."
)


def build_answers_response_schema(*, min_options: int, max_options: int) -> dict[str, Any]:
    """Schema for options only. Same ``correct_index`` device as the full quiz, for the same
    reason: it makes "exactly one correct answer" impossible to violate."""
    return {
        "type": "object",
        "properties": {
            "options": {
                "type": "array",
                "minItems": min_options,
                "maxItems": max_options,
                "items": {"type": "string"},
            },
            "correct_index": {
                "type": "integer",
                "description": "0-based index into options of the ONE correct answer.",
            },
            "citations": {
                "type": "array",
                "items": {"type": "integer"},
                "description": "Numbers [n] of the reference material used.",
            },
        },
        "required": ["options", "correct_index"],
    }


def build_answers_user_prompt(
    question_text: str, idea_text: str, context: str, option_count: int
) -> str:
    return (
        f"The teacher's question:\n{question_text}\n\n"
        f"The explanation they just gave in class:\n{idea_text}\n\n"
        f"Reference material from this course:\n{_material_block(context)}\n\n"
        f"Write {option_count} answer options for that question, exactly one of them correct. "
        "Respond with ONLY the JSON."
    )
