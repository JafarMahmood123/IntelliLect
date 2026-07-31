from __future__ import annotations

# Prompt templates for session summarization (S-1). Kept here (not scattered) so the
# structure and instructions are easy to tweak. English.
#
# The transcript decides WHICH topics the summary covers. The course material decides
# WHAT IS TRUE about them. That split matters: a live lecture is spoken from memory, and
# a misspoken number or a swapped definition would otherwise be carried into the PDF
# students revise from — the one artifact that outlives the session. So a conflict on a
# course-specific fact resolves to the course material, and is reported rather than
# silently smoothed over. Scope is still transcript-bound: material never adds topics.

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
    "are none.)\n"
    "## Corrections\n"
    "(Only where the transcript CONTRADICTS the supporting course material on a "
    "specific fact. One bullet each, in the form: **<topic>**: the course material "
    "states <correct version>. OMIT this whole section if there are no contradictions "
    "— which is the normal case.)"
)

SYSTEM_PROMPT = (
    "You are an expert teaching assistant who writes concise, accurate lecture "
    "summaries for students.\n\n"
    "The transcript sets the SCOPE: cover only topics the lecture actually taught, and "
    "never introduce a topic it did not cover.\n\n"
    "The supporting classroom material is AUTHORITATIVE on course-specific facts — "
    "figures and thresholds, which policy or method this course uses, definitions of "
    "named concepts, and stated requirements. Where the transcript and the material "
    "disagree on such a fact:\n"
    "1. State the MATERIAL's version in the body of the summary.\n"
    "2. Record the disagreement as one bullet under Corrections.\n\n"
    "Apply this ONLY to a direct, checkable contradiction on a topic the material "
    "actually addresses. If the material is silent, does not clearly conflict, or the "
    "point is an aside, a worked example, or an announcement (dates, logistics, "
    "administrative details), keep the transcript's version and add nothing to "
    "Corrections. Never guess at a correction, and never invent a conflict to fill the "
    "section — an empty Corrections section is the expected outcome.\n\n"
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
        "\n\nSupporting classroom material — AUTHORITATIVE on course-specific facts. Do "
        "NOT summarize it, and do NOT pull in topics from it that the lecture did not "
        "cover. Use it to get terminology right, and where it directly contradicts the "
        "transcript on a fact it actually addresses, state ITS version and note the "
        f"contradiction under Corrections:\n{supporting_material}"
    )


def build_single_pass_prompt(
    transcript: str, supporting_material: str | None = None
) -> str:
    """Prompt to summarize a full (short-enough) transcript in one pass."""
    return (
        "Summarize the following lecture transcript into the required Markdown "
        "structure. Cover only topics the transcript shows were taught.\n\n"
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
        "and cover only topics the notes support.\n\n"
        f"{joined}"
        f"{_supporting_block(supporting_material)}"
    )
