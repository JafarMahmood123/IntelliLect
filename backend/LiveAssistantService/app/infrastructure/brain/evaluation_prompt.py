"""The brain's evaluation prompt and context builder.

Kept here in code (not a template file) so it is easy to tweak. The prompt is
grounded (use ONLY the provided material), silence-biased (return no feedback when
uncertain), and forces a strict JSON reply.
"""

from __future__ import annotations

from app.domain.evaluation.retrieved_chunk import RetrievedChunk

SYSTEM_PROMPT = (
    "You are a private teaching assistant for a live class. You are given the "
    "teacher's spoken explanation and NUMBERED reference material from THIS course.\n\n"
    "Decide ONLY whether the explanation has a real problem RELATIVE TO THE MATERIAL:\n"
    "  - discrepancy: it factually conflicts with the material;\n"
    "  - gap: it misses or leaves out an important point the material covers;\n"
    "  - unclear: it states something in an unclear or ambiguous way.\n\n"
    "Rules:\n"
    "- Use ONLY the provided material. Never invent facts or use outside knowledge.\n"
    "- If there is no clear problem, return no feedback. Bias toward silence when "
    "uncertain — most explanations need no feedback.\n"
    "- The suggestion goes PRIVATELY to the teacher: be direct, concise, one short "
    "paragraph. Cite the material you rely on by [n].\n"
    "- Respond with ONLY a JSON object, no prose, no code fences.\n"
    "- Include your confidence in [0, 1] that this is a real problem worth raising.\n\n"
    'JSON schema: {"has_feedback": true|false, '
    '"type": "discrepancy|gap|unclear|none", '
    '"suggestion": "<one short paragraph, or empty>", "citations": [<n>, ...], '
    '"confidence": <number 0..1>}'
)

# SMOKE-TEST ONLY (temporary): a plain, friendly assistant turn (NOT the grounded evaluation
# prompt) used to eyeball that the configured brain is reachable and responding. Shared by every
# BrainClient so switching providers doesn't change the smoke behaviour.
SMOKE_SYSTEM_PROMPT = (
    "You are a helpful teaching assistant listening to a live lecture. "
    "The teacher just said something. Reply with ONE short, helpful remark "
    "or clarification (at most two sentences)."
)


def _location_label(chunk: RetrievedChunk) -> str:
    """A short source hint like " (slide 4)" / " (page 2)" / " (section Intro)"."""
    if chunk.page is not None:
        return f" (page {chunk.page})"
    if chunk.slide is not None:
        return f" (slide {chunk.slide})"
    if chunk.section:
        return f" (section {chunk.section})"
    return ""


def build_numbered_context(
    chunks: list[RetrievedChunk],
) -> tuple[str, dict[int, RetrievedChunk]]:
    """Render chunks as ``[n] (loc): text`` and return (context, {n: chunk}).

    The 1-based numbering is the citation map the model must cite against, so the
    same ordering maps a returned ``[n]`` back to its source chunk.
    """
    lines: list[str] = []
    citation_map: dict[int, RetrievedChunk] = {}
    for number, chunk in enumerate(chunks, start=1):
        citation_map[number] = chunk
        lines.append(f"[{number}]{_location_label(chunk)}: {chunk.text}")
    return "\n".join(lines), citation_map


def build_user_prompt(idea_text: str, context: str) -> str:
    return (
        f"Teacher's explanation:\n{idea_text}\n\n"
        f"Reference material:\n{context}\n\n"
        "Evaluate the explanation against the material and respond with ONLY the JSON."
    )
