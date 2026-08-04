from __future__ import annotations

import re
from uuid import uuid4

from app.application.services.token_counter import HeuristicTokenCounter
from app.domain.enums.chunk_source import ChunkSource
from app.domain.extraction.text_block import TextBlockSource
from app.infrastructure.chunking._text_splitter import Atom, RecursiveTextSplitter
from app.infrastructure.chunking.structural_chunker import StructuralChunker

from tests.chunking.fixtures import result, settings_for, text_block

DOC_ID = uuid4()
CLASS_ID = uuid4()


def _chunker(**overrides) -> StructuralChunker:
    return StructuralChunker(settings_for(**overrides), HeuristicTokenCounter())


def _sentences(text: str) -> list[str]:
    return [s.strip() for s in re.split(r"(?<=[.!?])\s+", text.strip()) if s.strip()]


async def test_respects_pages_max_index_and_metadata() -> None:
    page1 = " ".join(f"Alpha sentence number {i} about topic one." for i in range(1, 9))
    page2 = " ".join(f"Beta sentence number {i} about topic two." for i in range(1, 9))
    res = result("pdf", [text_block(0, page1, page=1), text_block(1, page2, page=2)])

    chunks = await _chunker(
        chunk_max_tokens=30, chunk_overlap_tokens=8, chunk_min_tokens=6
    ).chunk(res, DOC_ID, CLASS_ID)

    assert chunks
    # Global, sequential chunk_index from 0.
    assert [c.chunk_index for c in chunks] == list(range(len(chunks)))
    # Token cap respected (no merge overflow in this data).
    assert all(c.token_count <= 30 for c in chunks)
    # Location metadata present and correct.
    assert all(c.metadata.get("page") in (1, 2) for c in chunks)
    # Never cross a page boundary.
    for c in chunks:
        assert not ("Alpha" in c.text and "Beta" in c.text)
    # Page 1 chunks precede page 2 chunks, and page 1 produced several chunks.
    pages = [c.metadata["page"] for c in chunks]
    assert pages == sorted(pages)
    assert pages.count(1) >= 2
    # Native-only content is tagged TEXT.
    assert all(c.source == ChunkSource.TEXT for c in chunks)


async def test_consecutive_chunks_overlap() -> None:
    page = " ".join(f"Alpha sentence number {i} is present here now." for i in range(1, 10))
    res = result("pdf", [text_block(0, page, page=1)])

    chunks = await _chunker(
        chunk_max_tokens=30, chunk_overlap_tokens=8, chunk_min_tokens=6
    ).chunk(res, DOC_ID, CLASS_ID)

    assert len(chunks) >= 2
    # The first sentence of each chunk after the first is carried over from the prior.
    for earlier, later in zip(chunks, chunks[1:]):
        assert _sentences(later.text)[0] in earlier.text


async def test_merge_small_tail_absorbs_runt_deterministically() -> None:
    # Direct unit test of the tail-merge: a near-max atom + a tiny one that will not fit.
    counter = HeuristicTokenCounter()
    splitter = RecursiveTextSplitter(counter, max_tokens=46, overlap_tokens=0)
    big = Atom("x" * 180, TextBlockSource.NATIVE)  # ~45 tokens
    tiny = Atom("yy", TextBlockSource.NATIVE)  # 1 token, below min

    merged = splitter.merge_small_tail([[big], [tiny]], min_tokens=10)

    assert len(merged) == 1
    assert big in merged[0] and tiny in merged[0]


async def test_small_trailing_fragment_is_not_left_standalone() -> None:
    big = "Alpha " + " ".join(f"word{i}" for i in range(1, 40)) + "."
    page = big + " Tiny end."
    res = result("pdf", [text_block(0, page, page=1)])

    chunks = await _chunker(
        chunk_max_tokens=48, chunk_overlap_tokens=0, chunk_min_tokens=10
    ).chunk(res, DOC_ID, CLASS_ID)

    # "Tiny end." must be absorbed, never a chunk of its own.
    assert "Tiny end." in " ".join(c.text for c in chunks)
    assert all(c.text.strip() != "Tiny end." for c in chunks)
    # Merge overflow is bounded by min_tokens.
    assert all(c.token_count <= 48 + 10 for c in chunks)


async def test_native_and_ocr_on_same_page_merge_to_mixed() -> None:
    res = result(
        "pdf",
        [
            text_block(0, "Native line about the alpha topic.", page=1),
            text_block(1, "Recovered line about the alpha topic.", page=1, source=TextBlockSource.OCR),
        ],
    )

    chunks = await _chunker(
        chunk_max_tokens=512, chunk_overlap_tokens=64, chunk_min_tokens=64
    ).chunk(res, DOC_ID, CLASS_ID)

    assert len(chunks) == 1
    assert chunks[0].source == ChunkSource.MIXED
    assert chunks[0].metadata == {"page": 1}
    assert "Native line" in chunks[0].text and "Recovered line" in chunks[0].text


async def test_ocr_only_block_is_tagged_ocr() -> None:
    res = result(
        "pdf",
        [text_block(0, "Only recovered text about the beta topic.", page=2, source=TextBlockSource.OCR)],
    )

    chunks = await _chunker().chunk(res, DOC_ID, CLASS_ID)

    assert len(chunks) == 1
    assert chunks[0].source == ChunkSource.OCR
    assert chunks[0].metadata == {"page": 2}


async def test_empty_and_whitespace_blocks_are_ignored() -> None:
    res = result(
        "pdf",
        [
            text_block(0, "   ", page=1),
            text_block(1, "", page=1),
            text_block(2, "Real content lives on the page here.", page=1),
        ],
    )

    chunks = await _chunker().chunk(res, DOC_ID, CLASS_ID)

    assert len(chunks) == 1
    assert chunks[0].text == "Real content lives on the page here."


async def test_docx_sections_and_pptx_slides_are_boundaries() -> None:
    docx = result(
        "docx",
        [
            text_block(0, "Alpha content sits in the intro section.", section="Intro"),
            text_block(1, "Beta content sits in the goals section.", section="Intro > Goals"),
        ],
    )
    docx_chunks = await _chunker(chunk_max_tokens=512).chunk(docx, DOC_ID, CLASS_ID)
    assert [c.metadata for c in docx_chunks] == [
        {"section": "Intro"},
        {"section": "Intro > Goals"},
    ]

    pptx = result(
        "pptx",
        [
            text_block(0, "Alpha content on the first slide here.", slide=1),
            text_block(1, "Beta content on the second slide here.", slide=2),
        ],
    )
    pptx_chunks = await _chunker(chunk_max_tokens=512).chunk(pptx, DOC_ID, CLASS_ID)
    assert [c.metadata for c in pptx_chunks] == [{"slide": 1}, {"slide": 2}]
    # Sequential global index across groups.
    assert [c.chunk_index for c in pptx_chunks] == [0, 1]
