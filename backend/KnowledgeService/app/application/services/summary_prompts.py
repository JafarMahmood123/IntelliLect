from __future__ import annotations

# Prompt templates for session summarization (S-1). Kept here (not scattered) so the
# structure and instructions are easy to tweak. English. The model must summarize ONLY
# what the transcript shows was taught; supporting material sharpens terminology, it
# never adds untaught content.

# The stable Markdown contract every single-pass / synthesis summary must follow.
_STRUCTURE = (
    "# Session Summary\n"
    "## Overview\n"
    "(2-4 sentence high-level recap of what this lecture covered.)\n"
    "## Key Points\n"
    "(Bulleted list of the main things actually taught.)\n"
    "## Key Terms\n"
    "(Bulleted keywords / concepts, each with a one-line definition where useful.)\n"
    "## Notable Moments\n"
    "(Important explanations or points of emphasis; OMIT this whole section if there "
    "are none.)"
)

SYSTEM_PROMPT = (
    "You are an expert teaching assistant who writes concise, accurate lecture "
    "summaries for students. You summarize ONLY what the lecture transcript shows was "
    "actually taught — you never invent content, and you never add facts that are not "
    "in the transcript. When supporting classroom material is provided, use it ONLY to "
    "get terminology and concept names right, not to introduce topics the lecture did "
    "not cover.\n\n"
    "Always respond in GitHub-flavored Markdown using EXACTLY this structure:\n"
    f"{_STRUCTURE}\n\n"
    "Do not wrap the whole response in a code fence. Output Markdown only."
)

# System prompt for the map step (per-chunk note-taking). The structured Markdown
# contract does NOT apply here — these are intermediate notes fed to the synthesis step.
NOTES_SYSTEM_PROMPT = (
    "You take concise, faithful notes from a lecture transcript. Capture only what the "
    "text actually says — never invent or embellish. Output plain bullet points."
)

# Returned WITHOUT calling the model when the transcript is empty or too short.
INSUFFICIENT_CONTENT_MARKDOWN = (
    "# Session Summary\n\n"
    "_Insufficient content to summarize: the session transcript was empty or too "
    "short to produce a meaningful summary._\n"
)


def _supporting_block(supporting_material: str | None) -> str:
    if not supporting_material:
        return ""
    return (
        "\n\nSupporting classroom material (use ONLY to sharpen terminology — do NOT "
        "summarize this, and do NOT add anything from it that the lecture did not "
        f"actually cover):\n{supporting_material}"
    )


def build_single_pass_prompt(
    transcript: str, supporting_material: str | None = None
) -> str:
    """Prompt to summarize a full (short-enough) transcript in one pass."""
    return (
        "Summarize the following lecture transcript into the required Markdown "
        "structure. Summarize only what the transcript shows was taught.\n\n"
        f"Lecture transcript:\n{transcript}"
        f"{_supporting_block(supporting_material)}"
    )


def build_chunk_prompt(chunk: str, part_number: int, part_total: int) -> str:
    """Map step: condense one chunk of a long transcript into terse notes.

    Deliberately NOT the final structure — these partial notes are fed to the reduce
    (synthesis) step, which produces the structured summary.
    """
    return (
        f"This is part {part_number} of {part_total} of a longer lecture transcript. "
        "Write concise notes capturing the key points, concepts/terms, and any notable "
        "explanations in THIS part only. Do not add anything not present in the text. "
        "Plain bullet points, no headings.\n\n"
        f"Transcript part {part_number}/{part_total}:\n{chunk}"
    )


def build_synthesis_prompt(
    partial_notes: list[str], supporting_material: str | None = None
) -> str:
    """Reduce step: synthesize the per-chunk notes into the final structured summary."""
    joined = "\n\n".join(
        f"Notes from part {i + 1}:\n{note}" for i, note in enumerate(partial_notes)
    )
    return (
        "Below are ordered notes taken from consecutive parts of a single lecture. "
        "Synthesize them into ONE coherent summary using the required Markdown "
        "structure. Merge duplicates, keep the lecture's original ordering of ideas, "
        "and include only what the notes support.\n\n"
        f"{joined}"
        f"{_supporting_block(supporting_material)}"
    )
