"""WeasyPrintPdfRenderer (S-2) — valid PDF bytes, pages, graceful failure.

The PDF-producing tests skip cleanly when WeasyPrint's system libraries (pango/cairo/…)
are unavailable. The RenderingError test does NOT need them — it forces an internal
failure — so failure handling is always exercised.
"""

from __future__ import annotations

from datetime import date, datetime

import pytest

from app.application.ports.pdf_renderer import SummaryPdfMetadata
from app.infrastructure.rendering.weasyprint_pdf_renderer import (
    RenderingError,
    WeasyPrintPdfRenderer,
    weasyprint_available,
)

# Gate only the tests that actually produce a PDF.
requires_weasyprint = pytest.mark.skipif(
    not weasyprint_available(),
    reason="WeasyPrint system libraries (pango/cairo/gdk-pixbuf) not available",
)

_SAMPLE = """# Session Summary

## Overview
A two-sentence recap of the lecture. It covered the basics.

## Key Points
- First key point
- Second key point
- Third key point

## Key Terms
- **Chloroplast**: where photosynthesis happens.
- **Chlorophyll**: the green pigment.

## Notable Moments
- Cleared up a common misconception.
"""


def _page_count(pdf_bytes: bytes) -> int:
    import fitz  # pymupdf — already a project dependency

    with fitz.open(stream=pdf_bytes, filetype="pdf") as doc:
        return doc.page_count


@requires_weasyprint
def test_render_returns_valid_pdf_with_at_least_one_page():
    pdf = WeasyPrintPdfRenderer().render(_SAMPLE, SummaryPdfMetadata())

    assert isinstance(pdf, bytes) and pdf  # non-empty
    assert pdf.startswith(b"%PDF")         # PDF magic header
    assert _page_count(pdf) >= 1


@requires_weasyprint
def test_render_with_full_metadata_produces_valid_pdf():
    metadata = SummaryPdfMetadata(
        title="Session Summary",
        session_date=date(2026, 7, 15),
        classroom_name="Biology 101",
        generated_at=datetime(2026, 7, 15, 9, 30),
    )
    pdf = WeasyPrintPdfRenderer().render(_SAMPLE, metadata)

    assert pdf.startswith(b"%PDF")
    assert _page_count(pdf) >= 1


@requires_weasyprint
def test_empty_markdown_still_renders_a_valid_pdf():
    # Empty input is handled gracefully: a minimal but valid PDF, not a crash.
    pdf = WeasyPrintPdfRenderer().render("   ", SummaryPdfMetadata())

    assert pdf.startswith(b"%PDF")
    assert _page_count(pdf) >= 1


def test_render_failure_raises_rendering_error(monkeypatch):
    """A failure in the HTML->PDF step surfaces as a catchable RenderingError."""
    renderer = WeasyPrintPdfRenderer()

    def _boom(_html: str) -> bytes:
        raise RuntimeError("weasyprint exploded")

    monkeypatch.setattr(renderer, "_html_to_pdf", _boom)

    with pytest.raises(RenderingError) as excinfo:
        renderer.render(_SAMPLE, SummaryPdfMetadata())
    assert "weasyprint exploded" in str(excinfo.value)


def test_render_error_on_empty_output_bytes(monkeypatch):
    """No PDF bytes from the backend is treated as a failure, not a silent empty file."""
    renderer = WeasyPrintPdfRenderer()
    monkeypatch.setattr(renderer, "_html_to_pdf", lambda _html: b"")

    with pytest.raises(RenderingError):
        renderer.render(_SAMPLE, SummaryPdfMetadata())
