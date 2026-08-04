"""HTML/template step of the summary renderer (S-2) — no WeasyPrint, always runs.

The Markdown->HTML conversion and the styled-document assembly are pure string work,
so they are unit-tested without the PDF system libraries.
"""

from __future__ import annotations

from datetime import date, datetime

from app.application.ports.pdf_renderer import SummaryPdfMetadata
from app.infrastructure.rendering.weasyprint_pdf_renderer import (
    WeasyPrintPdfRenderer,
    markdown_to_html,
)

_SAMPLE = """# Session Summary

## Overview
A short recap of the lecture.

## Key Points
- First point
- Second point

## Key Terms
- **Term**: a definition.
"""


def test_markdown_to_html_emits_expected_tags():
    html = markdown_to_html(_SAMPLE)
    assert "<h1>" in html
    assert "<h2>" in html
    assert "<ul>" in html and "<li>" in html
    assert "<strong>" in html  # inline emphasis survives


def test_empty_markdown_yields_placeholder_body_not_blank():
    assert "No summary content" in markdown_to_html("   ")
    assert "No summary content" in markdown_to_html("")


def test_to_html_wraps_body_and_includes_structure():
    html = WeasyPrintPdfRenderer().to_html(_SAMPLE, SummaryPdfMetadata())
    assert html.lstrip().startswith("<!DOCTYPE html>")
    assert 'class="doc-body"' in html
    assert "<h2>" in html  # the section headings are inside the document body
    assert "Session Summary" in html  # default title in the header


def test_metadata_is_reflected_in_the_html():
    metadata = SummaryPdfMetadata(
        title="Session Summary",
        session_date=date(2026, 7, 15),
        classroom_name="Biology 101",
        generated_at=datetime(2026, 7, 15, 9, 30),
    )
    html = WeasyPrintPdfRenderer().to_html("# Session Summary\n\ncontent", metadata)

    assert "Classroom: Biology 101" in html
    assert "Session date: July 15, 2026" in html      # subheader
    assert "Generated on July 15, 2026" in html        # footer (CSS margin box)


def test_metadata_is_html_escaped():
    metadata = SummaryPdfMetadata(classroom_name="Math & <Science>")
    html = WeasyPrintPdfRenderer().to_html("# t\n\nbody", metadata)
    assert "Math &amp; &lt;Science&gt;" in html
    assert "<Science>" not in html


def test_no_metadata_omits_subheader():
    html = WeasyPrintPdfRenderer().to_html("# t\n\nbody", SummaryPdfMetadata())
    assert 'class="doc-subheader"' not in html
