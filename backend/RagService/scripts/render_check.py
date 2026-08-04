"""Render a Markdown summary to a styled PDF and write it to disk (S-2), OFFLINE.

Eyeball the PDF styling — sections, header/footer, metadata — with no models, no
services, no S3. Uses a built-in sample summary, or a Markdown file you pass in.

Usage (from the RagService directory):

    python scripts/render_check.py                       # sample -> ./summary.pdf
    python scripts/render_check.py --out /tmp/s.pdf
    python scripts/render_check.py path/to/summary.md --out /tmp/s.pdf
"""

from __future__ import annotations

import argparse
import sys
from datetime import date, datetime
from pathlib import Path

from app.application.ports.pdf_renderer import SummaryPdfMetadata
from app.infrastructure.rendering.weasyprint_pdf_renderer import (
    RenderingError,
    WeasyPrintPdfRenderer,
    weasyprint_available,
)

_SAMPLE_MARKDOWN = """# Session Summary

## Overview
This lecture introduced photosynthesis: how plants convert light energy into chemical
energy, where it happens, and its two main stages.

## Key Points
- Photosynthesis converts light energy into glucose.
- It occurs in the chloroplasts, which contain the pigment chlorophyll.
- **Stage 1** — the light-dependent reactions split water and produce ATP and NADPH.
- **Stage 2** — the Calvin cycle fixes CO2 into glucose using ATP and NADPH.

## Key Terms
- **Chloroplast**: the organelle where photosynthesis occurs.
- **Chlorophyll**: the green pigment that absorbs light (`red` and `blue`).
- **Calvin cycle**: the carbon-fixation stage of photosynthesis.

## Notable Moments
- Clarified the common misconception that plants only respire at night.
"""


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Render a Markdown summary to a styled PDF (offline)."
    )
    parser.add_argument(
        "markdown_file", nargs="?", type=Path, default=None,
        help="Markdown file to render (defaults to a built-in sample summary).",
    )
    parser.add_argument(
        "--out", type=Path, default=Path("summary.pdf"), help="Output PDF path."
    )
    parser.add_argument("--classroom", default="Biology 101", help="Classroom name.")
    args = parser.parse_args(argv)

    if not weasyprint_available():
        print(
            "WeasyPrint is not usable here (missing system libs like pango/cairo). "
            "Install them or run inside the service container.",
            file=sys.stderr,
        )
        return 2

    markdown_text = (
        args.markdown_file.read_text(encoding="utf-8")
        if args.markdown_file
        else _SAMPLE_MARKDOWN
    )
    metadata = SummaryPdfMetadata(
        title="Session Summary",
        session_date=date.today(),
        classroom_name=args.classroom,
        generated_at=datetime.now(),
    )

    try:
        pdf_bytes = WeasyPrintPdfRenderer().render(markdown_text, metadata)
    except RenderingError as exc:
        print(f"Rendering failed: {exc}", file=sys.stderr)
        return 1

    args.out.write_bytes(pdf_bytes)
    print(f"Wrote {args.out}  ({len(pdf_bytes):,} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
