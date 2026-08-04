"""Eyeball the selective OCR cascade on a real file.

Usage (from the RagService directory):

    python scripts/ocr_preview.py path/to/scanned.pdf
    python scripts/ocr_preview.py path/to/deck.pptx

Runs Phase 2 extraction (ExtractorRouter) then the Phase 3 OCR cascade
(TesseractOcrProcessor) and prints ONLY the OCR-derived blocks — location, mean
word confidence, and a text snippet — so you can judge OCR quality. Requires the
tesseract binary; no models or database. Native (Phase 2) blocks are not printed.
"""

from __future__ import annotations

import argparse
import asyncio
import sys
from pathlib import Path

from app.application.ports.extractor import ExtractionError
from app.domain.extraction.text_block import TextBlock, TextBlockSource
from app.infrastructure.config.settings import get_settings
from app.infrastructure.extraction.router import ExtractorRouter
from app.infrastructure.ocr.tesseract_ocr_processor import (
    TesseractOcrProcessor,
    tesseract_available,
)

_CONTENT_TYPES = {
    ".pdf": "application/pdf",
    ".docx": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    ".pptx": "application/vnd.openxmlformats-officedocument.presentationml.presentation",
}


def _location(block: TextBlock) -> str:
    if block.page is not None:
        return f"page {block.page}"
    if block.slide is not None:
        return f"slide {block.slide}"
    return "-"


def _snippet(text: str, width: int = 100) -> str:
    collapsed = " ".join(text.split())
    return collapsed if len(collapsed) <= width else collapsed[: width - 1] + "…"


async def _run(path: Path) -> int:
    content_type = _CONTENT_TYPES.get(path.suffix.lower())
    router = ExtractorRouter.default()
    try:
        result = router.extract(path.read_bytes(), path.name, content_type)
    except ExtractionError as exc:
        print(f"Extraction failed: {exc}", file=sys.stderr)
        return 1

    processor = TesseractOcrProcessor(get_settings())
    enriched = await processor.process(path.read_bytes(), result)

    ocr_blocks = [b for b in enriched.blocks if b.source == TextBlockSource.OCR]
    print(f"File:          {path}")
    print(f"Source format: {enriched.source_format}")
    print(f"Native blocks: {len(enriched.blocks) - len(ocr_blocks)}")
    print(f"OCR blocks:    {len(ocr_blocks)}")
    print(f"Tesseract calls: {processor.last_ocr_invocations}  "
          f"(cache hits: {processor.last_cache_hits})")

    print("\n--- OCR-derived blocks ---")
    if not ocr_blocks:
        print("  (none)")
    for block in ocr_blocks:
        confidence = processor.last_confidence_by_order.get(block.order, 0.0)
        print(
            f"  #{block.order:<3} {_location(block):<10} conf={confidence:5.1f}  "
            f"{_snippet(block.text)}"
        )
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Preview the OCR cascade on a document.")
    parser.add_argument("path", type=Path, help="Path to a .pdf, .docx, or .pptx file")
    args = parser.parse_args(argv)

    path: Path = args.path
    if not path.is_file():
        print(f"No such file: {path}", file=sys.stderr)
        return 2
    if not tesseract_available():
        print(
            "tesseract binary not found on PATH. Install tesseract-ocr and "
            "tesseract-ocr-eng, or run inside the service container.",
            file=sys.stderr,
        )
        return 3

    return asyncio.run(_run(path))


if __name__ == "__main__":
    raise SystemExit(main())
