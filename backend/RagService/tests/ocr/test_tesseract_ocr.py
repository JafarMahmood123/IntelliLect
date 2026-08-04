from __future__ import annotations

import pytest

from app.domain.extraction.extraction_result import ExtractionResult
from app.domain.extraction.text_block import TextBlock, TextBlockKind, TextBlockSource
from app.infrastructure.config.settings import Settings
from app.infrastructure.extraction.pdf_extractor import PdfExtractor
from app.infrastructure.ocr.tesseract_ocr_processor import (
    TesseractOcrProcessor,
    tesseract_available,
)

from tests.ocr.fixtures import (
    extracted_image,
    make_icon_image,
    make_noise_image,
    make_scanned_pdf,
    make_text_image,
    normalize,
)

# The whole module needs the tesseract binary; skip cleanly when it is absent.
pytestmark = pytest.mark.skipif(
    not tesseract_available(),
    reason="tesseract binary not installed (install tesseract-ocr + tesseract-ocr-eng)",
)


def _processor(**overrides) -> TesseractOcrProcessor:
    """Build a processor. `overrides` tune OCR_* settings; DATABASE_URL comes from env."""
    return TesseractOcrProcessor(Settings(**overrides))


def _ocr_blocks(result: ExtractionResult) -> list[TextBlock]:
    return [b for b in result.blocks if b.source == TextBlockSource.OCR]


async def test_scanned_pdf_page_is_recovered() -> None:
    pdf = make_scanned_pdf()
    result = PdfExtractor().extract(pdf, "scan.pdf")
    # Phase 2 sanity: the page has no text layer and one full-page image.
    assert result.pages_without_text == [1]
    assert result.blocks == []
    assert len(result.images) == 1 and result.images[0].covers_page

    processor = _processor()
    enriched = await processor.process(pdf, result)

    ocr = _ocr_blocks(enriched)
    assert len(ocr) == 1
    block = ocr[0]
    assert block.page == 1
    assert block.slide is None
    assert block.kind == TextBlockKind.PARAGRAPH
    assert block.source == TextBlockSource.OCR
    assert "quick brown fox" in normalize(block.text)
    # The full-page image must NOT be OCR'd again as an embedded image.
    assert processor.last_ocr_invocations == 1


async def test_embedded_image_with_text_is_recovered() -> None:
    image = extracted_image(make_text_image(), slide=2)
    result = ExtractionResult(source_format="pptx", images=[image], page_count=2)

    processor = _processor()
    enriched = await processor.process(b"", result)

    ocr = _ocr_blocks(enriched)
    assert len(ocr) == 1
    assert ocr[0].slide == 2
    assert ocr[0].page is None
    assert ocr[0].source == TextBlockSource.OCR
    assert "quick brown fox" in normalize(ocr[0].text)


async def test_small_icon_is_skipped_before_ocr() -> None:
    image = extracted_image(make_icon_image(48), slide=1)  # 48px < OCR_MIN_IMAGE_PX
    result = ExtractionResult(source_format="pptx", images=[image])

    processor = _processor()
    enriched = await processor.process(b"", result)

    assert _ocr_blocks(enriched) == []
    assert processor.last_ocr_invocations == 0  # size-gated: tesseract never called


async def test_noise_image_is_dropped_by_confidence_filter() -> None:
    image = extracted_image(make_noise_image(), slide=1)  # 400px passes the size gate
    result = ExtractionResult(source_format="pptx", images=[image])

    # A strict confidence floor makes the filter's rejection deterministic.
    processor = _processor(ocr_min_confidence=95)
    enriched = await processor.process(b"", result)

    assert _ocr_blocks(enriched) == []
    # Tesseract WAS invoked (the image cleared the size gate) but output was filtered.
    assert processor.last_ocr_invocations == 1


async def test_repeated_image_is_ocrd_once_but_placed_everywhere() -> None:
    png = make_text_image()
    first = extracted_image(png, order=0, slide=1)
    second = extracted_image(png, order=1, slide=2)  # identical bytes -> same sha256
    result = ExtractionResult(source_format="pptx", images=[first, second])

    processor = _processor()
    enriched = await processor.process(b"", result)

    ocr = _ocr_blocks(enriched)
    assert len(ocr) == 2
    assert {b.slide for b in ocr} == {1, 2}
    assert all("quick brown fox" in normalize(b.text) for b in ocr)
    # OCR ran once; the second location was served from the sha256 cache.
    assert processor.last_ocr_invocations == 1
    assert processor.last_cache_hits == 1


async def test_ocr_blocks_are_appended_after_native_blocks() -> None:
    native = TextBlock(
        order=0,
        text="native body",
        kind=TextBlockKind.PARAGRAPH,
        slide=1,
        source=TextBlockSource.NATIVE,
    )
    image = extracted_image(make_text_image(), slide=1)
    result = ExtractionResult(source_format="pptx", blocks=[native], images=[image])

    processor = _processor()
    enriched = await processor.process(b"", result)

    # Native block is untouched and stays first.
    assert enriched.blocks[0] is native
    ocr = _ocr_blocks(enriched)
    assert len(ocr) == 1
    # Global order continues past the native block's order.
    assert ocr[0].order == 1


async def test_missing_tesseract_returns_result_unchanged(monkeypatch) -> None:
    # Simulate the binary being unavailable: process() must fail open (no OCR blocks).
    monkeypatch.setattr(
        "app.infrastructure.ocr.tesseract_ocr_processor.tesseract_available",
        lambda: False,
    )
    image = extracted_image(make_text_image(), slide=1)
    result = ExtractionResult(source_format="pptx", images=[image])

    processor = _processor()
    enriched = await processor.process(b"", result)

    assert _ocr_blocks(enriched) == []
    assert processor.last_ocr_invocations == 0
