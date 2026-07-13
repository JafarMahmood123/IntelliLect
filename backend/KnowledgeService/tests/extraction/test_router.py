from __future__ import annotations

import pytest

from app.application.ports.extractor import UnsupportedFormatError
from app.infrastructure.extraction.router import ExtractorRouter

from tests.extraction.fixtures import (
    DOCX_CONTENT_TYPE,
    PDF_CONTENT_TYPE,
    PPTX_CONTENT_TYPE,
    make_docx,
    make_pptx,
    make_text_pdf,
)


def test_router_dispatches_by_content_type() -> None:
    router = ExtractorRouter.default()

    assert router.extract(make_text_pdf(), "a.pdf", PDF_CONTENT_TYPE).source_format == "pdf"
    assert router.extract(make_docx(), "a.docx", DOCX_CONTENT_TYPE).source_format == "docx"
    assert router.extract(make_pptx(), "a.pptx", PPTX_CONTENT_TYPE).source_format == "pptx"


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


def test_router_supports() -> None:
    router = ExtractorRouter.default()
    assert router.supports(PDF_CONTENT_TYPE) is True
    assert router.supports(DOCX_CONTENT_TYPE) is True
    assert router.supports("image/png") is False


def test_router_unknown_format_raises() -> None:
    router = ExtractorRouter.default()
    with pytest.raises(UnsupportedFormatError):
        router.extract(b"hello", "notes.txt", "text/plain")
