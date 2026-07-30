from __future__ import annotations

import codecs
import re

from app.application.ports.extractor import CorruptFileError, Extractor
from app.domain.extraction.extraction_result import ExtractionResult
from app.domain.extraction.text_block import TextBlock, TextBlockKind
from app.infrastructure.extraction._support import (
    TXT_CONTENT_TYPES,
    TXT_EXTENSIONS,
    OrderCounter,
    logger,
    normalize_content_type,
)

# Byte-order marks, LONGEST FIRST. BOM_UTF32_LE (ff fe 00 00) starts with BOM_UTF16_LE
# (ff fe), so checking UTF-16 first would decode a UTF-32 file as UTF-16 and produce
# text interleaved with NUL characters instead of failing loudly.
_BOM_CODECS: tuple[tuple[bytes, str], ...] = (
    (codecs.BOM_UTF32_LE, "utf-32"),
    (codecs.BOM_UTF32_BE, "utf-32"),
    (codecs.BOM_UTF8, "utf-8-sig"),
    (codecs.BOM_UTF16_LE, "utf-16"),
    (codecs.BOM_UTF16_BE, "utf-16"),
)

# A run of one or more blank lines separates paragraphs. Line endings are normalized to
# "\n" before this is applied, so it never has to match \r.
_PARAGRAPH_BREAK = re.compile(r"\n[ \t]*\n\s*")


class TxtExtractor(Extractor):
    """Extractor for plain-text (.txt) and Markdown (.md) files.

    The other three formats carry structure the extractor can recover — pages, slides,
    heading styles. Plain text carries none, so this deliberately does the minimum: split
    on blank lines into PARAGRAPH blocks and stop. In particular Markdown syntax is NOT
    parsed; a "## Overview" line becomes ordinary paragraph text.

    That is not a shortcut so much as the honest mapping. Nothing downstream would use the
    extra structure: the chunker's `_boundary_key` has no case for "txt" and falls back to
    a single whole-document group, and it never reads `TextBlockKind` at all. Blank-line
    paragraphs give the chunker's atomizer clean split points, which is the only thing it
    actually needs. If Markdown headings ever need to survive into chunk metadata, that is
    a chunking change first, and this extractor would follow.

    A long block is safe: the atomizer splits recursively until each piece fits the token
    budget, so a file with no blank lines at all still chunks correctly — it just loses the
    paragraph hints.

    There are no images and no page count, so OCR (Phase 3) skips these documents entirely
    — its page and image candidate lists are both empty for a non-PDF with no images.
    """

    content_types = TXT_CONTENT_TYPES
    extensions = TXT_EXTENSIONS

    def supports(self, content_type: str) -> bool:
        return normalize_content_type(content_type) in self.content_types

    def extract(self, file_bytes: bytes, file_name: str) -> ExtractionResult:
        text = _decode(file_bytes, file_name)
        order = OrderCounter()
        blocks = [
            TextBlock(
                order=order.next(),
                text=paragraph,
                kind=TextBlockKind.PARAGRAPH,
            )
            for paragraph in _paragraphs(text)
        ]

        result = ExtractionResult(
            source_format="txt",
            blocks=blocks,
            images=[],
            page_count=None,
        )
        logger.info("Extracted txt: %d blocks", len(result.blocks))
        return result


def _decode(file_bytes: bytes, file_name: str) -> str:
    """Decode text bytes, honouring a BOM and degrading rather than failing.

    Order matters: a BOM is authoritative, so it wins outright. Only in its absence do we
    guess, and the guess is deliberately lenient — a teacher's notes saved from Notepad in
    a legacy codepage should ingest with a few mangled accents, not fail the whole upload.
    A file that is not text at all is a different case and does fail, so an uploaded binary
    renamed to .txt surfaces as a clear error rather than as a document full of garbage.
    """
    for bom, codec in _BOM_CODECS:
        if file_bytes.startswith(bom):
            try:
                return file_bytes.decode(codec)
            except UnicodeDecodeError as exc:
                raise CorruptFileError(
                    f"{file_name!r} carries a {codec} BOM but is not valid {codec}: {exc}"
                ) from exc

    # No BOM. NUL bytes mean binary — real text in any single-byte or UTF-8 encoding has
    # none. This also (correctly) rejects BOM-less UTF-16, which is not reliably detectable.
    if b"\x00" in file_bytes:
        raise CorruptFileError(
            f"{file_name!r} contains NUL bytes, so it is not a text file "
            "(a binary uploaded with a .txt name, or BOM-less UTF-16)."
        )

    try:
        return file_bytes.decode("utf-8")
    except UnicodeDecodeError:
        # cp1252, not latin-1: it is what Windows editors actually write, and it maps the
        # 0x80-0x9f range to the smart quotes and dashes that show up in pasted material.
        logger.warning(
            "%s is not valid UTF-8; decoding as cp1252 with replacement characters.",
            file_name,
        )
        return file_bytes.decode("cp1252", errors="replace")


def _paragraphs(text: str) -> list[str]:
    """Split into blank-line-separated paragraphs, dropping empties.

    Internal newlines inside a paragraph are preserved — hard-wrapped prose and short list
    items both read correctly to an embedding model, and collapsing them would silently
    join list items into one run-on line.
    """
    normalized = text.replace("\r\n", "\n").replace("\r", "\n")
    return [
        paragraph
        for paragraph in (part.strip() for part in _PARAGRAPH_BREAK.split(normalized))
        if paragraph
    ]
