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
    "- Never invent facts or use outside knowledge. If the explanation does not support a "
    "question, write fewer questions rather than padding.\n"
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
                "minItems": 1,
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


def build_user_prompt(idea_text: str, context: str, question_count: int) -> str:
    """The turn: what was just taught, what the course says, and how many questions to write."""
    material = context.strip() or (
        "(none found for this explanation — rely only on the teacher's words above, "
        "and leave citations empty)"
    )
    return (
        f"The teacher has just finished explaining:\n{idea_text}\n\n"
        f"Reference material from this course:\n{material}\n\n"
        f"Write at most {question_count} multiple-choice question(s) checking whether students "
        "understood that explanation. Respond with ONLY the JSON."
    )
