"""Eyeball the extraction layer on a real file.

Usage (from the KnowledgeService directory):

    python scripts/extract_preview.py path/to/file.pdf
    python scripts/extract_preview.py path/to/deck.pptx
    python scripts/extract_preview.py path/to/report.docx

Runs the ExtractorRouter over the file's bytes and prints the ordered text blocks
(kind + location + a snippet) followed by the embedded-image inventory. No models,
database, or network required — this exercises the pure extraction layer only.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from app.application.ports.extractor import ExtractionError
from app.domain.extraction.extraction_result import ExtractionResult
from app.domain.extraction.text_block import TextBlock
from app.infrastructure.extraction.router import ExtractorRouter

# Extension -> MIME, so the run exercises content-type dispatch (with the router's
# extension fallback as a safety net for anything not listed here).
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
    if block.section:
        return f"[{block.section}]"
    return "-"


def _snippet(text: str, width: int = 80) -> str:
    collapsed = " ".join(text.split())
    return collapsed if len(collapsed) <= width else collapsed[: width - 1] + "…"


def _print_result(path: Path, result: ExtractionResult) -> None:
    print(f"File:          {path}")
    print(f"Source format: {result.source_format}")
    print(f"Page count:    {result.page_count}")
    print(f"Blocks:        {len(result.blocks)}")
    print(f"Images:        {len(result.images)}")
    if result.pages_without_text:
        print(f"Pages w/o text: {result.pages_without_text}")

    print("\n--- Text blocks (reading order) ---")
    if not result.blocks:
        print("  (none)")
    for block in result.blocks:
        print(
            f"  #{block.order:<3} {block.kind.value:<9} {_location(block):<14} "
            f"{_snippet(block.text)}"
        )

    print("\n--- Image inventory ---")
    if not result.images:
        print("  (none)")
    for image in result.images:
        location = (
            f"page {image.page}"
            if image.page is not None
            else f"slide {image.slide}"
            if image.slide is not None
            else "-"
        )
        covers = " covers-page" if image.covers_page else ""
        print(
            f"  #{image.order:<3} {image.ext:<5} {image.width}x{image.height:<6} "
            f"{location:<10} sha={image.sha256[:12]}…{covers}"
        )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Preview extraction of a document.")
    parser.add_argument("path", type=Path, help="Path to a .pdf, .docx, or .pptx file")
    args = parser.parse_args(argv)

    path: Path = args.path
    if not path.is_file():
        print(f"No such file: {path}", file=sys.stderr)
        return 2

    content_type = _CONTENT_TYPES.get(path.suffix.lower())
    router = ExtractorRouter.default()
    try:
        result = router.extract(path.read_bytes(), path.name, content_type)
    except ExtractionError as exc:
        print(f"Extraction failed: {exc}", file=sys.stderr)
        return 1

    _print_result(path, result)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
