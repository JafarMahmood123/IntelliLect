from __future__ import annotations

from dataclasses import dataclass

from app.application.dtos.search_dtos import SearchResultItem
from app.application.services.token_counter import TokenCounter


@dataclass
class Citation:
    """A chunk included in the context, with the [n] number the model can cite."""

    number: int
    item: SearchResultItem


@dataclass
class PackedContext:
    text: str  # the numbered, source-tagged context block
    citations: list[Citation]  # only the chunks that fit the budget, in order


def _source_tag(item: SearchResultItem) -> str:
    """A short human-readable location tag for a chunk, for the model to cite."""
    metadata = item.metadata or {}
    if metadata.get("page") is not None:
        return f"page {metadata['page']}"
    if metadata.get("slide") is not None:
        return f"slide {metadata['slide']}"
    if metadata.get("section"):
        return f"section: {metadata['section']}"
    return "document"


class ContextBuilder:
    """Packs best-first retrieved chunks into a numbered context within a token budget.

    Chunks arrive best-first; they are numbered [1], [2], … and included in order
    until CONTEXT_MAX_TOKENS is reached, dropping the lowest-ranked overflow. At least
    the top chunk is always included so the context is never empty for a non-empty
    result set.
    """

    def __init__(self, token_counter: TokenCounter, context_max_tokens: int) -> None:
        self._counter = token_counter
        self._max_tokens = max(1, context_max_tokens)

    def build(self, results: list[SearchResultItem]) -> PackedContext:
        citations: list[Citation] = []
        entries: list[str] = []
        total_tokens = 0

        for item in results:
            number = len(citations) + 1
            entry = self._format_entry(number, item)
            entry_tokens = self._counter.count(entry)
            # Keep the top chunk unconditionally; gate the rest on the budget.
            if citations and total_tokens + entry_tokens > self._max_tokens:
                break
            citations.append(Citation(number=number, item=item))
            entries.append(entry)
            total_tokens += entry_tokens

        return PackedContext(text="\n\n".join(entries), citations=citations)

    @staticmethod
    def _format_entry(number: int, item: SearchResultItem) -> str:
        return f"[{number}] ({_source_tag(item)}): {item.text}"
