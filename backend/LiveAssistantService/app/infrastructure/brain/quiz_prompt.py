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

# Shared by both prompts, so the rule that decides what counts as the RIGHT answer cannot end up
# stated one way for a whole quiz and another way for a single question's options.
#
# The ordering matters. A teacher mid-lesson can misspeak — a number, a direction, a definition —
# and a quiz built faithfully on that mistake marks the whole class wrong for listening. So the
# material wins where it actually addresses the point. The second half is the load-bearing half:
# without it, "the material overrules the teacher" becomes licence to overrule from the model's own
# knowledge whenever retrieval is thin, which is the failure this service is built to avoid.
_ACCURACY_RULES = (
    "WHAT COUNTS AS CORRECT:\n"
    "- Where the reference material speaks to a point, the MATERIAL is authoritative — even when "
    "the teacher said otherwise. Teachers misspeak. A quiz built on a slip marks the whole class "
    "wrong for having listened.\n"
    "- So: if the explanation conflicts with the material, write the question so the option the "
    "MATERIAL supports is the correct one, and put the teacher's version among the wrong options "
    "if it makes a plausible distractor.\n"
    "- Then REPORT every such conflict in the `corrections` field: `taught` quotes what the "
    "teacher said, `corrected` states what the material says. The teacher reads these before "
    "publishing. Never change an answer without recording it here.\n"
    "- You may ONLY overrule the teacher from the material in front of you. If the material does "
    "not cover the point, is only loosely related, or is empty, the teacher is right by default — "
    "take their explanation as given and leave `corrections` empty. Your own knowledge is not "
    "evidence, and a disagreement you cannot cite is not a correction.\n"
    "- Differences of wording, emphasis, simplification or level of detail are NOT conflicts. "
    "Report only a genuine contradiction of fact.\n"
)

SYSTEM_PROMPT = (
    "You are a teaching assistant helping a teacher quiz their class during a live lesson.\n\n"
    "You are given the explanation the teacher has JUST finished giving, and NUMBERED reference "
    "material from THIS course.\n\n"
    "Write multiple-choice questions that check whether students understood THAT explanation.\n\n"
    f"{_ACCURACY_RULES}\n"
    "Rules:\n"
    "- Question ONLY the SUBJECT the teacher explained. The reference material fixes what is TRUE "
    "about that subject — it is not a second syllabus to pick new topics from.\n"
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


# Shared by both schemas. An ARRAY rather than an optional object on purpose: "empty list" is a
# shape every provider's constrained decoding handles cleanly, and it makes "nothing to report" the
# default the model falls into rather than a field it has to remember to omit.
_CORRECTIONS_SCHEMA: dict[str, Any] = {
    "type": "array",
    "description": (
        "Points where the reference material contradicts the teacher. Empty unless the material "
        "directly covers the point and genuinely disagrees."
    ),
    "items": {
        "type": "object",
        "properties": {
            "taught": {
                "type": "string",
                "description": "What the teacher said, quoted as closely as possible.",
            },
            "corrected": {
                "type": "string",
                "description": "What the reference material says instead.",
            },
        },
        "required": ["taught", "corrected"],
    },
}


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
            "corrections": _CORRECTIONS_SCHEMA,
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
    idea_text: str,
    context: str,
    question_count: int,
    avoid: list[str] | None = None,
    *,
    whole_session: bool = False,
) -> str:
    """The turn: what was taught, what the course says, and how many questions to write.

    ``whole_session`` swaps a quick test on the last explanation for a quiz over the entire
    lesson. The wording has to change with it: told "the teacher has just finished explaining"
    over a full transcript, the model reliably writes every question about the final minute.
    """
    plural = "question" if question_count == 1 else "questions"
    if whole_session:
        return (
            f"The complete lesson the teacher has taught in this session:\n{idea_text}\n\n"
            f"Reference material from this course:\n{_material_block(context)}"
            f"{_avoid_block(avoid)}\n\n"
            f"Write EXACTLY {question_count} multiple-choice {plural} covering this lesson AS A "
            "WHOLE. Spread them ACROSS the lesson rather than clustering on any one part, and "
            "favour the points the teacher spent the most time on. Respond with ONLY the JSON."
        )
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
    f"{_ACCURACY_RULES}\n"
    "Rules:\n"
    "- Answer the teacher's question as asked. Do not reinterpret or rewrite it.\n"
    "- Exactly one option is correct, chosen by the rule above: the material decides where it "
    "covers the point, the explanation decides where it does not, and outside knowledge never "
    "decides.\n"
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
            "corrections": _CORRECTIONS_SCHEMA,
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
