from __future__ import annotations

from app.domain.extraction.text_block import TextBlockKind
from app.infrastructure.extraction.pptx_extractor import PptxExtractor

from tests.extraction.fixtures import PPTX_CONTENT_TYPE, make_pptx


def test_pptx_blocks_kinds_and_slide_tagging() -> None:
    result = PptxExtractor().extract(make_pptx(), "deck.pptx")

    assert result.source_format == "pptx"
    assert result.page_count == 2

    tagged = [(b.kind, b.text, b.slide) for b in result.blocks]
    assert tagged == [
        (TextBlockKind.TITLE, "First Slide", 1),
        (TextBlockKind.PARAGRAPH, "Body of the first slide.", 1),
        (TextBlockKind.NOTES, "Remember to explain the intro.", 1),
        (TextBlockKind.TITLE, "Second Slide", 2),
        (TextBlockKind.PARAGRAPH, "Body of the second slide.", 2),
    ]

    # PPTX carries no page/section tags.
    assert all(b.page is None and b.section is None for b in result.blocks)

    orders = [b.order for b in result.blocks]
    assert orders == sorted(orders)


def test_pptx_image_inventory() -> None:
    result = PptxExtractor().extract(make_pptx(), "deck.pptx")

    assert len(result.images) == 1
    image = result.images[0]
    assert image.slide == 1
    assert image.page is None
    assert (image.width, image.height) == (32, 20)
    assert image.ext == "png"
    assert len(image.sha256) == 64
    assert image.covers_page is False


def test_pptx_supports_content_type() -> None:
    extractor = PptxExtractor()
    assert extractor.supports(PPTX_CONTENT_TYPE) is True
    assert extractor.supports("text/plain") is False
