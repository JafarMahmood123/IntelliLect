from __future__ import annotations

from uuid import uuid4

from app.application.services.token_counter import HeuristicTokenCounter
from app.infrastructure.chunking.semantic_chunker import SemanticChunker

from tests.chunking.fixtures import (
    FakeEmbeddingProvider,
    result,
    settings_for,
    text_block,
)

DOC_ID = uuid4()
CLASS_ID = uuid4()

# Orthogonal topic vectors -> cosine distance 1 across a shift, 0 within a topic.
TOPICS = {"cats": [1.0, 0.0, 0.0, 0.0], "finance": [0.0, 1.0, 0.0, 0.0]}


async def test_breakpoint_is_placed_at_topic_shift() -> None:
    cats = "Cats purr softly. Cats chase mice. Cats nap all day."
    finance = "Finance markets rose. Finance rates climbed. Finance bonds fell."
    res = result("pdf", [text_block(0, f"{cats} {finance}", page=1)])
    fake = FakeEmbeddingProvider(TOPICS)

    chunker = SemanticChunker(
        settings_for(
            chunk_max_tokens=512,
            chunk_overlap_tokens=0,
            semantic_breakpoint_percentile=90,
        ),
        fake,
        HeuristicTokenCounter(),
    )
    chunks = await chunker.chunk(res, DOC_ID, CLASS_ID)

    # One breakpoint at the shift -> exactly two chunks, split by topic.
    assert len(chunks) == 2
    assert "Cats" in chunks[0].text and "Finance" not in chunks[0].text
    assert "Finance" in chunks[1].text and "Cats" not in chunks[1].text
    assert all(c.token_count <= 512 for c in chunks)
    assert all(c.metadata == {"page": 1} for c in chunks)
    # One embedding batch for the single group.
    assert fake.embed_documents_calls == 1


async def test_semantic_never_crosses_page_boundary() -> None:
    res = result(
        "pdf",
        [
            text_block(0, "Cats purr softly. Cats chase mice.", page=1),
            text_block(1, "Cats nap all day. Cats hunt at night.", page=2),
        ],
    )
    fake = FakeEmbeddingProvider(TOPICS)

    chunker = SemanticChunker(settings_for(), fake, HeuristicTokenCounter())
    chunks = await chunker.chunk(res, DOC_ID, CLASS_ID)

    # Same topic within each page -> one chunk per page, never merged across pages.
    assert {c.metadata["page"] for c in chunks} == {1, 2}
    for c in chunks:
        assert c.text  # non-empty
    # One embedding batch per group (page).
    assert fake.embed_documents_calls == 2


async def test_oversized_same_topic_span_falls_back_to_structural_split() -> None:
    long_text = " ".join(f"Cats sentence number {i} is here right now today." for i in range(1, 12))
    res = result("pdf", [text_block(0, long_text, page=1)])
    fake = FakeEmbeddingProvider(TOPICS)

    chunker = SemanticChunker(
        settings_for(chunk_max_tokens=30, chunk_overlap_tokens=8),
        fake,
        HeuristicTokenCounter(),
    )
    chunks = await chunker.chunk(res, DOC_ID, CLASS_ID)

    # No topic shift -> one big span -> structural fallback splits it under the cap.
    assert len(chunks) >= 2
    assert all(c.token_count <= 30 for c in chunks)
    assert all(c.metadata == {"page": 1} for c in chunks)
    assert [c.chunk_index for c in chunks] == list(range(len(chunks)))
