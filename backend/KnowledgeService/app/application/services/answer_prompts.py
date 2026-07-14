from __future__ import annotations

# Grounded, citation-aware, refusal-safe prompting. Kept here (not scattered) so it
# is easy to tweak.

SYSTEM_PROMPT = (
    "You are a teaching assistant for a classroom. Answer the student's question "
    "using ONLY the provided context, which is drawn from that classroom's uploaded "
    "materials. Rules:\n"
    "- If the answer is not contained in the context, say you don't have that "
    "information in the classroom materials — do not guess or use outside knowledge.\n"
    "- Cite the sources you used by their bracketed numbers, e.g. [1], [2].\n"
    "- Be concise and accurate."
)

# Returned WITHOUT calling the model when retrieval finds nothing.
NO_CONTEXT_ANSWER = (
    "I don't have any relevant material in this classroom to answer that question."
)


def build_user_prompt(context: str, question: str) -> str:
    return (
        "Context from the classroom materials (each source is numbered):\n"
        f"{context}\n\n"
        f"Question: {question}\n\n"
        "Answer using only the context above, and cite the sources you used by their "
        "[n] numbers."
    )
