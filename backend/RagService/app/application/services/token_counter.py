from __future__ import annotations

from typing import Protocol, runtime_checkable


@runtime_checkable
class TokenCounter(Protocol):
    """Counts tokens in a piece of text.

    A protocol so the chunker can be handed any counter — the offline heuristic
    below by default, or a real tokenizer (e.g. the embedding model's) once one is
    wired in, without changing the chunkers.
    """

    def count(self, text: str) -> int:
        ...


class HeuristicTokenCounter:
    """Deterministic, dependency-free token estimate (~4 characters per token).

    Good enough to bound chunk sizes offline. No tiktoken/transformers — swap in a
    model-accurate counter later via the TokenCounter protocol if needed.
    """

    def count(self, text: str) -> int:
        length = len(text)
        if length == 0:
            return 0
        return max(1, round(length / 4))
