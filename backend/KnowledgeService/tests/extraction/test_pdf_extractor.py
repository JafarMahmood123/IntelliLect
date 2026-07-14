from __future__ import annotations

import pytest

from app.application.ports.extractor import CorruptFileError
from app.domain.extraction.text_block import TextBlockKind
from app.infrastructure.extraction.pdf_extractor import PdfExtractor

from tests.extraction.fixtures import make_scanned_pdf, make_text_pdf


def test_text_pdf_blocks_pages_and_order() -> None:
    result = PdfExtractor().extract(make_text_pdf(), "notes.pdf")

    assert result.source_format == "pdf"
    assert result.page_count == 2
    assert result.pages_without_text == []
    assert result.images == []

    assert result.blocks, "expected text blocks from a text PDF"
    assert all(b.kind == TextBlockKind.PARAGRAPH for b in result.blocks)
    assert {b.page for b in result.blocks} == {1, 2}

    # Reading order: everything on page 1 precedes page 2.
    page1_orders = [b.order for b in result.blocks if b.page == 1]
    page2_orders = [b.order for b in result.blocks if b.page == 2]
    assert max(page1_orders) < min(page2_orders)

    text = "\n".join(b.text for b in result.blocks)
    assert "Chapter One" in text
    assert "first paragraph" in text
    assert "Chapter Two" in text


def test_scanned_pdf_has_no_text_and_full_page_image() -> None:
    result = PdfExtractor().extract(make_scanned_pdf(), "scan.pdf")

    assert result.page_count == 1
    assert result.pages_without_text == [1]
    assert result.blocks == []

    assert len(result.images) == 1
    image = result.images[0]
    assert image.page == 1
    assert image.covers_page is True
    assert image.width > 0 and image.height > 0
    assert len(image.sha256) == 64


def test_corrupt_pdf_raises() -> None:
    with pytest.raises(CorruptFileError):
        PdfExtractor().extract(b"%PDF-1.4 not really a pdf", "broken.pdf")
