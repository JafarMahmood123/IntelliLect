from __future__ import annotations

from uuid import uuid4

from app.application.dtos.search_dtos import SearchResultItem
from app.application.services.context_builder import ContextBuilder
from app.application.services.token_counter import HeuristicTokenCounter


def _item(text: str, *, score: float = 0.9, chunk_index: int = 0, **metadata) -> SearchResultItem:
    return SearchResultItem(
        chunk_id=uuid4(),
        document_id=uuid4(),
        text=text,
        score=score,
        chunk_index=chunk_index,
        metadata=metadata,
    )


def test_entries_are_numbered_and_source_tagged() -> None:
    builder = ContextBuilder(HeuristicTokenCounter(), context_max_tokens=6000)
    items = [
        _item("Photosynthesis converts light.", page=2),
        _item("Chlorophyll absorbs light.", slide=4),
        _item("The cell wall is rigid.", section="Cells > Structure"),
        _item("No location here."),
    ]

    packed = builder.build(items)

    assert [c.number for c in packed.citations] == [1, 2, 3, 4]
    assert [c.item for c in packed.citations] == items
    assert "[1] (page 2): Photosynthesis converts light." in packed.text
    assert "[2] (slide 4): Chlorophyll absorbs light." in packed.text
    assert "[3] (section: Cells > Structure): The cell wall is rigid." in packed.text
    assert "[4] (document): No location here." in packed.text


def test_budget_drops_lowest_ranked_overflow() -> None:
    # Each entry is ~30 tokens; a 70-token budget fits exactly the top two.
    text = "word " * 25  # ~125 chars -> entry ~ (125 + tag) / 4 ≈ 34 tokens
    items = [
        _item(text, score=0.95, page=1),
        _item(text, score=0.85, page=2),
        _item(text, score=0.75, page=3),
        _item(text, score=0.65, page=4),
    ]
    builder = ContextBuilder(HeuristicTokenCounter(), context_max_tokens=70)

    packed = builder.build(items)

    # Only the best-ranked chunks that fit are kept; the rest are dropped in order.
    assert [c.number for c in packed.citations] == [1, 2]
    assert [c.item for c in packed.citations] == items[:2]
    assert HeuristicTokenCounter().count(packed.text) <= 70


def test_at_least_the_top_chunk_is_kept_even_if_over_budget() -> None:
    builder = ContextBuilder(HeuristicTokenCounter(), context_max_tokens=1)
    items = [_item("A fairly long single chunk that exceeds the tiny budget.", page=1)]

    packed = builder.build(items)

    assert len(packed.citations) == 1  # never returns empty context for a hit


def test_empty_results_produce_empty_context() -> None:
    builder = ContextBuilder(HeuristicTokenCounter(), context_max_tokens=6000)
    packed = builder.build([])
    assert packed.text == ""
    assert packed.citations == []
