from __future__ import annotations

import hashlib

from app.domain.extraction.text_block import TextBlockKind
from app.infrastructure.extraction.docx_extractor import DocxExtractor

from tests.extraction.fixtures import DOCX_CONTENT_TYPE, make_docx, make_png


def test_docx_blocks_kinds_order_and_sections() -> None:
    result = DocxExtractor().extract(make_docx(), "sample.docx")

    assert result.source_format == "docx"
    assert result.page_count is None

    kinds = [(b.kind, b.text, b.section) for b in result.blocks]
    assert kinds == [
        (TextBlockKind.HEADING, "Introduction", "Introduction"),
        (TextBlockKind.HEADING, "Goals", "Introduction > Goals"),
        (TextBlockKind.PARAGRAPH, "The goal is to extract text.", "Introduction > Goals"),
        (
            TextBlockKind.TABLE,
            "Name | Value\nAlpha | 1",
            "Introduction > Goals",
        ),
    ]

    # DOCX carries no page/slide tags.
    assert all(b.page is None and b.slide is None for b in result.blocks)

    # Global reading order is a contiguous, ascending sequence.
    orders = [b.order for b in result.blocks]
    assert orders == sorted(orders)
    assert orders == list(range(len(orders)))


def test_docx_image_inventory() -> None:
    result = DocxExtractor().extract(make_docx(), "sample.docx")

    assert len(result.images) == 1
    image = result.images[0]
    assert image.ext == "png"
    assert (image.width, image.height) == (24, 16)
    assert image.sha256 == hashlib.sha256(make_png()).hexdigest()
    # DOCX images carry no page/slide and are never marked as covering a page.
    assert image.page is None and image.slide is None
    assert image.covers_page is False
    # Image order continues past the text blocks.
    assert image.order > max(b.order for b in result.blocks)


def test_docx_supports_content_type() -> None:
    extractor = DocxExtractor()
    assert extractor.supports(DOCX_CONTENT_TYPE) is True
    assert extractor.supports("application/pdf") is False
