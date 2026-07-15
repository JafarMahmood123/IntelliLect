"""A tiny, dependency-free text-similarity function for duplicate suppression (LA-7).

Token Jaccard over lowercased word sets — no embeddings needed. It is injected into
the ``FeedbackPacer`` behind a small callable so it can be upgraded later (e.g. to a
cosine similarity over embeddings) without touching the pacing rules.
"""

from __future__ import annotations

import re

_TOKEN = re.compile(r"[a-z0-9]+")


def _tokens(text: str) -> set[str]:
    return set(_TOKEN.findall(text.lower()))


def jaccard_similarity(a: str, b: str) -> float:
    """|A ∩ B| / |A ∪ B| over word tokens; 1.0 for two empty strings, 0.0 if one is empty."""
    ta, tb = _tokens(a), _tokens(b)
    if not ta and not tb:
        return 1.0
    if not ta or not tb:
        return 0.0
    intersection = len(ta & tb)
    union = len(ta | tb)
    return intersection / union
