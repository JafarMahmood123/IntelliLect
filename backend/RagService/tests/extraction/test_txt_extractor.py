"""Cover the plain-text extractor: paragraph splitting and encoding tolerance.

The extractor itself is trivial, so nearly all of the risk lives in `_decode`. A .txt is
the one format a user can produce in any editor on any platform, which means it is the one
format that arrives in encodings nobody chose deliberately — a BOM from Notepad, cp1252
smart quotes from pasted material, or an actual binary someone renamed. Those cases are
where this file spends its assertions.
"""

from __future__ import annotations

import pytest

from app.application.ports.extractor import CorruptFileError
from app.domain.extraction.text_block import TextBlockKind, TextBlockSource
from app.infrastructure.extraction.txt_extractor import TxtExtractor

from tests.extraction.fixtures import (
    MARKDOWN_CONTENT_TYPE,
    PDF_CONTENT_TYPE,
    TXT_CONTENT_TYPE,
)

SAMPLE = "First paragraph.\n\nSecond paragraph.\n\n\nThird paragraph.\n"


def _texts(file_bytes: bytes, file_name: str = "notes.txt") -> list[str]:
    return [block.text for block in TxtExtractor().extract(file_bytes, file_name).blocks]


# --- structure ---------------------------------------------------------------------


def test_splits_on_blank_lines() -> None:
    assert _texts(SAMPLE.encode()) == [
        "First paragraph.",
        "Second paragraph.",
        "Third paragraph.",
    ]


def test_result_shape() -> None:
    result = TxtExtractor().extract(SAMPLE.encode(), "notes.txt")

    assert result.source_format == "txt"
    assert result.page_count is None
    # No images and no pages_without_text means OCR skips the document entirely.
    assert result.images == []
    assert result.pages_without_text == []
    assert [block.order for block in result.blocks] == [0, 1, 2]
    assert all(block.kind is TextBlockKind.PARAGRAPH for block in result.blocks)
    assert all(block.source is TextBlockSource.NATIVE for block in result.blocks)
    # Plain text has no page/slide/section, so chunks carry empty location metadata.
    assert all(
        (block.page, block.slide, block.section) == (None, None, None)
        for block in result.blocks
    )


def test_preserves_newlines_inside_a_paragraph() -> None:
    """Collapsing these would run consecutive list items together into one line."""
    assert _texts(b"- alpha\n- beta\n- gamma\n\nAfter.") == ["- alpha\n- beta\n- gamma", "After."]


def test_handles_crlf_and_lone_cr_line_endings() -> None:
    assert _texts(b"One.\r\n\r\nTwo.") == ["One.", "Two."]
    assert _texts(b"One.\r\rTwo.") == ["One.", "Two."]


def test_blank_line_with_trailing_whitespace_still_separates() -> None:
    """Editors leave spaces on "blank" lines; a literal \\n\\n check would miss these."""
    assert _texts(b"One.\n   \nTwo.\n\t\nThree.") == ["One.", "Two.", "Three."]


def test_a_file_with_no_blank_lines_is_one_block() -> None:
    """Not a failure: the chunker's atomizer splits a long block to fit the token budget."""
    assert _texts(b"Line one.\nLine two.\nLine three.") == [
        "Line one.\nLine two.\nLine three."
    ]


def test_empty_and_whitespace_only_files_yield_no_blocks() -> None:
    assert _texts(b"") == []
    assert _texts(b"\n\n   \n\t\n") == []


def test_markdown_syntax_is_kept_as_literal_text() -> None:
    """Documents the deliberate choice: nothing downstream reads heading structure."""
    blocks = TxtExtractor().extract(b"# Overview\n\nBody text.", "notes.md").blocks

    assert [b.text for b in blocks] == ["# Overview", "Body text."]
    assert blocks[0].kind is TextBlockKind.PARAGRAPH


# --- encoding ----------------------------------------------------------------------


def test_reads_utf8_without_a_bom() -> None:
    assert _texts("Café — naïve.".encode()) == ["Café — naïve."]


def test_strips_a_utf8_bom() -> None:
    """A leftover BOM would ride along in the first chunk and be embedded as noise."""
    assert _texts(b"\xef\xbb\xbfHello.") == ["Hello."]


def test_reads_utf16_via_its_bom() -> None:
    assert _texts("Hello.\n\nWorld.".encode("utf-16")) == ["Hello.", "World."]


def test_reads_utf32_via_its_bom() -> None:
    """UTF-32 LE begins with the UTF-16 LE BOM, so BOM order decides this one."""
    assert _texts("Hello.".encode("utf-32")) == ["Hello."]


def test_falls_back_to_cp1252_instead_of_failing() -> None:
    """A legacy-codepage file should ingest, not fail the upload."""
    # 0x93/0x94 are cp1252 smart quotes and are not valid UTF-8.
    assert _texts(b"He said \x93hello\x94.") == ["He said “hello”."]


def test_binary_content_is_rejected() -> None:
    with pytest.raises(CorruptFileError, match="NUL bytes"):
        TxtExtractor().extract(b"\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR", "logo.txt")


# --- dispatch ----------------------------------------------------------------------


def test_supports_text_content_types() -> None:
    extractor = TxtExtractor()

    assert extractor.supports(TXT_CONTENT_TYPE) is True
    assert extractor.supports(MARKDOWN_CONTENT_TYPE) is True
    assert extractor.supports("text/plain; charset=utf-8") is True
    assert extractor.supports(PDF_CONTENT_TYPE) is False
