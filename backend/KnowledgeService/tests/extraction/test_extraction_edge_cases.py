from __future__ import annotations

import io

import pymupdf
import pytest
from docx import Document as open_docx

from app.application.ports.extractor import CorruptFileError
from app.infrastructure.extraction.docx_extractor import DocxExtractor
from app.infrastructure.extraction.pdf_extractor import PdfExtractor

from tests.extraction.fixtures import make_png


def _encrypted_pdf() -> bytes:
    document = pymupdf.open()
    page = document.new_page()
    page.insert_text((72, 72), "locked content")
    data = document.tobytes(
        encryption=pymupdf.PDF_ENCRYPT_AES_256, owner_pw="pw", user_pw="pw"
    )
    document.close()
    return data


def _docx_image_only() -> bytes:
    document = open_docx()
    document.add_picture(io.BytesIO(make_png(width=300, height=220)))
    buffer = io.BytesIO()
    document.save(buffer)
    return buffer.getvalue()


def _docx_text_only() -> bytes:
    document = open_docx()
    document.add_paragraph("Just some text with no pictures at all here.")
    buffer = io.BytesIO()
    document.save(buffer)
    return buffer.getvalue()


def test_empty_pdf_raises_corrupt() -> None:
    with pytest.raises(CorruptFileError):
        PdfExtractor().extract(b"", "empty.pdf")


def test_password_protected_pdf_raises_corrupt() -> None:
    with pytest.raises(CorruptFileError):
        PdfExtractor().extract(_encrypted_pdf(), "locked.pdf")


def test_document_with_images_but_no_text() -> None:
    result = DocxExtractor().extract(_docx_image_only(), "image-only.docx")

    assert result.blocks == []  # no text anywhere
    assert len(result.images) == 1  # the embedded picture is still inventoried


def test_text_document_with_zero_images() -> None:
    result = DocxExtractor().extract(_docx_text_only(), "text-only.docx")

    assert result.images == []
    assert any("no pictures" in block.text for block in result.blocks)
