"""The chunking edges a normal document never reaches, and the strategy switch.

Every one of these is a real shape a real upload produces — a title slide with one sentence, a
page of images with no text at all, a file format the grouper has no rule for. They are grouped
here because none of them is interesting enough for its own file, and all of them return empty
or degenerate input into code that otherwise assumes a paragraph.
"""

from __future__ import annotations

from uuid import uuid4

import pytest

from app.application.services.token_counter import HeuristicTokenCounter
from app.domain.extraction.text_block import TextBlockSource
from app.infrastructure.chunking._text_splitter import Atom
from app.infrastructure.chunking.factory import create_chunker
from app.infrastructure.chunking.semantic_chunker import (
    SemanticChunker,
    _cosine_similarity,
    _percentile,
)
from app.infrastructure.chunking.structural_chunker import StructuralChunker

from tests.chunking.fixtures import FakeEmbeddingProvider, result, settings_for, text_block

ORTHOGONAL = {"alpha": [1.0, 0.0], "beta": [0.0, 1.0]}


def _semantic(**overrides) -> tuple[SemanticChunker, FakeEmbeddingProvider]:
    embedder = FakeEmbeddingProvider(ORTHOGONAL, default=[1.0, 0.0])
    settings = settings_for(chunking_strategy="semantic", **overrides)
    return SemanticChunker(settings, embedder, HeuristicTokenCounter()), embedder


async def _chunks(chunker, blocks, source_format="pdf"):
    return await chunker.chunk(
        result(source_format, blocks), document_id=uuid4(), classroom_id=uuid4()
    )


# --- the strategy switch ------------------------------------------------------------------


def test_the_configured_strategy_picks_the_chunker():
    embedder = FakeEmbeddingProvider(ORTHOGONAL)

    semantic = create_chunker(settings_for(chunking_strategy="semantic"), embedder)
    structural = create_chunker(settings_for(chunking_strategy="structural"), embedder)

    assert isinstance(semantic, SemanticChunker)
    assert isinstance(structural, StructuralChunker)


@pytest.mark.parametrize("configured", ["  SEMANTIC  ", "Semantic", "semantic"])
def test_the_strategy_name_tolerates_case_and_stray_whitespace(configured):
    # It arrives from an environment variable, which is where trailing spaces and capitals come
    # from. Silently falling back to structural would change how the corpus is chunked without
    # anything saying so.
    chunker = create_chunker(
        settings_for(chunking_strategy=configured), FakeEmbeddingProvider(ORTHOGONAL)
    )

    assert isinstance(chunker, SemanticChunker)


def test_an_unrecognised_strategy_falls_back_to_the_offline_one():
    """Fail toward the strategy that needs no model.

    A typo in CHUNKING_STRATEGY should not select a chunker that requires a live embedder for
    every ingestion, and should not stop the service from starting either.
    """
    chunker = create_chunker(
        settings_for(chunking_strategy="sematnic"), FakeEmbeddingProvider(ORTHOGONAL)
    )

    assert isinstance(chunker, StructuralChunker)


# --- degenerate documents -----------------------------------------------------------------


async def test_a_page_with_no_text_produces_no_chunks_and_no_embedding_call():
    """An image-only page — a scan whose OCR found nothing, a slide that is one photograph.

    An empty chunk would be indexed, embedded and billed, and would then match queries on the
    strength of nothing at all.
    """
    chunker, embedder = _semantic()

    chunks = await _chunks(chunker, [text_block(0, "   \n  ", page=1)])

    assert chunks == []
    assert embedder.embed_documents_calls == 0


async def test_a_single_sentence_is_chunked_without_asking_the_model():
    # A title slide. There is no pair of sentences to measure a distance between, so there is
    # nothing for the embedder to decide — and calling it anyway costs a request per slide.
    chunker, embedder = _semantic()

    chunks = await _chunks(chunker, [text_block(0, "Introduction to photosynthesis.", page=1)])

    assert [c.text for c in chunks] == ["Introduction to photosynthesis."]
    assert embedder.embed_documents_calls == 0


async def test_a_structural_document_with_nothing_in_it_yields_nothing():
    chunker = StructuralChunker(settings_for(), HeuristicTokenCounter())

    assert await _chunks(chunker, [text_block(0, "", page=1)]) == []


async def test_a_format_with_no_grouping_rule_falls_back_to_one_document_group():
    """`txt` has no pages, slides or sections.

    The grouper keys on the source format; an unhandled one must group the whole file rather
    than crash or emit a chunk per block.
    """
    chunker = StructuralChunker(settings_for(chunk_max_tokens=100), HeuristicTokenCounter())

    chunks = await _chunks(
        chunker,
        [text_block(0, "First paragraph."), text_block(1, "Second paragraph.")],
        source_format="txt",
    )

    assert len(chunks) == 1
    assert "First paragraph." in chunks[0].text and "Second paragraph." in chunks[0].text


# --- the arithmetic underneath ------------------------------------------------------------


def test_a_zero_vector_scores_no_similarity_rather_than_dividing_by_zero():
    # An embedder that returns zeros for a sentence it cannot represent would otherwise take
    # down the whole ingestion at the breakpoint calculation.
    assert _cosine_similarity([0.0, 0.0], [1.0, 0.0]) == 0.0
    assert _cosine_similarity([1.0, 0.0], [0.0, 0.0]) == 0.0


def test_identical_and_orthogonal_vectors_sit_at_the_ends_of_the_scale():
    assert _cosine_similarity([1.0, 0.0], [1.0, 0.0]) == pytest.approx(1.0)
    assert _cosine_similarity([1.0, 0.0], [0.0, 1.0]) == pytest.approx(0.0)


def test_the_breakpoint_percentile_handles_the_degenerate_inputs():
    # Zero distances happens with one sentence; one distance happens with two. Both are common
    # in slides, and both would otherwise index out of range.
    assert _percentile([], 90) == 0.0
    assert _percentile([0.4], 90) == 0.4


def test_the_breakpoint_percentile_interpolates_between_samples():
    # p50 of four evenly spaced values lands between the middle two.
    assert _percentile([0.0, 1.0, 2.0, 3.0], 50) == pytest.approx(1.5)
    # A rank that lands exactly on a sample returns that sample.
    assert _percentile([0.0, 1.0, 2.0, 3.0], 100) == pytest.approx(3.0)
    assert _percentile([0.0, 1.0, 2.0, 3.0], 0) == pytest.approx(0.0)


async def test_a_span_too_large_for_the_budget_falls_back_to_structural_splitting():
    """The semantic tier only decides *where* topics change; the token cap still rules.

    One long uninterrupted topic would otherwise become a single chunk far over the budget,
    which the embedder silently truncates.
    """
    chunker, _ = _semantic(chunk_max_tokens=10, chunk_overlap_tokens=0)
    long_paragraph = " ".join(f"alpha sentence number {i}." for i in range(20))

    chunks = await _chunks(chunker, [text_block(0, long_paragraph, page=1)])

    counter = HeuristicTokenCounter()
    assert len(chunks) > 1
    # The relaxation from merge_small_tail is bounded; nothing should be wildly over.
    assert all(counter.count(chunk.text) <= 20 for chunk in chunks)


async def test_a_topic_shift_starts_a_new_chunk():
    # The reason this strategy exists: the breakpoint lands where the subject changes, not
    # where the token count happens to run out.
    chunker, _ = _semantic(chunk_max_tokens=200, semantic_breakpoint_percentile=50)

    chunks = await _chunks(
        chunker,
        [
            text_block(
                0,
                "Alpha one. Alpha two. Beta one. Beta two.",
                page=1,
            )
        ],
    )

    assert len(chunks) == 2
    assert chunks[0].text.lower().startswith("alpha")
    assert "beta" in chunks[1].text.lower()


def test_an_empty_span_emits_nothing():
    chunker, _ = _semantic()

    assert chunker._emit_span([]) == []


def test_a_span_that_fits_is_emitted_whole():
    chunker, _ = _semantic(chunk_max_tokens=100)
    span = [Atom(text="a short sentence", source=TextBlockSource.NATIVE)]

    assert chunker._emit_span(span) == [span]
