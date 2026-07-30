from __future__ import annotations

import pytest

from app.application.ports.extractor import UnsupportedFormatError
from app.infrastructure.extraction.router import ExtractorRouter

from tests.extraction.fixtures import (
    DOCX_CONTENT_TYPE,
    MARKDOWN_CONTENT_TYPE,
    PDF_CONTENT_TYPE,
    PPTX_CONTENT_TYPE,
    TXT_CONTENT_TYPE,
    make_docx,
    make_png,
    make_pptx,
    make_text_pdf,
)


def test_router_dispatches_by_content_type() -> None:
    router = ExtractorRouter.default()

    assert router.extract(make_text_pdf(), "a.pdf", PDF_CONTENT_TYPE).source_format == "pdf"
    assert router.extract(make_docx(), "a.docx", DOCX_CONTENT_TYPE).source_format == "docx"
    assert router.extract(make_pptx(), "a.pptx", PPTX_CONTENT_TYPE).source_format == "pptx"
    assert router.extract(b"Hello.", "a.txt", TXT_CONTENT_TYPE).source_format == "txt"
    assert router.extract(b"# Hello", "a.md", MARKDOWN_CONTENT_TYPE).source_format == "txt"


def test_router_normalizes_content_type_parameters() -> None:
    router = ExtractorRouter.default()
    result = router.extract(make_text_pdf(), "a.pdf", "application/pdf; charset=binary")
    assert result.source_format == "pdf"


def test_router_falls_back_to_extension() -> None:
    router = ExtractorRouter.default()
    # No content type / an unhelpful one -> dispatch on the file extension.
    assert router.extract(make_text_pdf(), "report.pdf").source_format == "pdf"
    result = router.extract(make_docx(), "report.docx", "application/octet-stream")
    assert result.source_format == "docx"
    # Browsers often report no content type at all for .md — the extension has to carry it.
    assert router.extract(b"# Notes", "notes.md").source_format == "txt"


def test_router_supports() -> None:
    router = ExtractorRouter.default()
    assert router.supports(PDF_CONTENT_TYPE) is True
    assert router.supports(DOCX_CONTENT_TYPE) is True
    assert router.supports(TXT_CONTENT_TYPE) is True
    assert router.supports("image/png") is False


def test_router_unknown_format_raises() -> None:
    router = ExtractorRouter.default()
    with pytest.raises(UnsupportedFormatError):
        router.extract(make_png(), "logo.png", "image/png")
