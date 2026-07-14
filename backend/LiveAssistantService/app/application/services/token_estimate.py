"""A tiny, deterministic token estimator for boundary length caps.

Whitespace word count — good enough to bound idea length (``BOUNDARY_MIN_TOKENS`` /
``BOUNDARY_MAX_TOKENS``) without pulling in a tokenizer dependency. A model-accurate
counter can replace this later without touching the boundary logic.
"""

from __future__ import annotations


def estimate_tokens(text: str) -> int:
    """Approximate token count of ``text`` as its whitespace-separated word count."""
    return len(text.split())
