from __future__ import annotations

import io
import zipfile
from collections.abc import Iterator

from docx import Document as open_docx
from docx.document import Document as DocxDocument
from docx.oxml.table import CT_Tbl
from docx.oxml.text.paragraph import CT_P
from docx.table import Table
from docx.text.paragraph import Paragraph

from app.application.ports.extractor import CorruptFileError, Extractor
from app.domain.extraction.extracted_image import ExtractedImage
from app.domain.extraction.extraction_result import ExtractionResult
from app.domain.extraction.text_block import TextBlock, TextBlockKind
from app.infrastructure.extraction._support import (
    DOCX_CONTENT_TYPES,
    DOCX_EXTENSIONS,
    OrderCounter,
    logger,
    normalize_content_type,
    read_image_dimensions,
    sha256_hex,
)

# Extensions of embedded media Pillow cannot decode (vector art). Skipped, logged.
_UNREADABLE_IMAGE_EXTS = frozenset({"wmf", "emf", "svg", "eps"})
_MEDIA_PREFIX = "word/media/"


class DocxExtractor(Extractor):
    """Extractor for Word (.docx) files, backed by python-docx.

    Walks the document body in order: heading-styled paragraphs become HEADING
    blocks and drive a running section path; normal paragraphs become PARAGRAPH
    blocks tagged with that path; each table becomes one TABLE block. Embedded
    images are read from the package's `word/media/` and hashed.
    """

    content_types = DOCX_CONTENT_TYPES
    extensions = DOCX_EXTENSIONS

    def supports(self, content_type: str) -> bool:
        return normalize_content_type(content_type) in self.content_types

    def extract(self, file_bytes: bytes, file_name: str) -> ExtractionResult:
        try:
            document = open_docx(io.BytesIO(file_bytes))
        except Exception as exc:  # PackageNotFoundError / bad zip / bad xml
            raise CorruptFileError(
                f"Could not open DOCX {file_name!r}: {exc}"
            ) from exc

        order = OrderCounter()
        blocks = self._body_blocks(document, order)
        images = self._images(file_bytes, order)

        result = ExtractionResult(
            source_format="docx",
            blocks=blocks,
            images=images,
            page_count=None,
        )
        _log_summary(result)
        return result

    def _body_blocks(
        self, document: DocxDocument, order: OrderCounter
    ) -> list[TextBlock]:
        blocks: list[TextBlock] = []
        heading_stack: list[str] = []

        for item in _iter_body(document):
            if isinstance(item, Paragraph):
                block = self._paragraph_block(item, heading_stack, order)
                if block is not None:
                    blocks.append(block)
            else:  # Table
                text = _table_to_text(item)
                if not text:
                    continue
                blocks.append(
                    TextBlock(
                        order=order.next(),
                        text=text,
                        kind=TextBlockKind.TABLE,
                        section=_section_path(heading_stack),
                    )
                )
        return blocks

    @staticmethod
    def _paragraph_block(
        paragraph: Paragraph, heading_stack: list[str], order: OrderCounter
    ) -> TextBlock | None:
        text = paragraph.text.strip()
        heading_level = _heading_level(paragraph)

        if heading_level is not None:
            if not text:
                return None
            # Replace everything at or below this level, then push this heading.
            del heading_stack[heading_level:]
            heading_stack.append(text)
            return TextBlock(
                order=order.next(),
                text=text,
                kind=TextBlockKind.HEADING,
                section=_section_path(heading_stack),
            )

        if not text:
            return None
        return TextBlock(
            order=order.next(),
            text=text,
            kind=TextBlockKind.PARAGRAPH,
            section=_section_path(heading_stack),
        )

    @staticmethod
    def _images(file_bytes: bytes, order: OrderCounter) -> list[ExtractedImage]:
        images: list[ExtractedImage] = []
        with zipfile.ZipFile(io.BytesIO(file_bytes)) as package:
            names = sorted(
                name
                for name in package.namelist()
                if name.startswith(_MEDIA_PREFIX) and not name.endswith("/")
            )
            for name in names:
                filename_ext = name.rsplit(".", 1)[-1].lower() if "." in name else ""
                if filename_ext in _UNREADABLE_IMAGE_EXTS:
                    logger.warning(
                        "Skipping vector image %s (Pillow cannot decode .%s).",
                        name,
                        filename_ext,
                    )
                    continue
                data = package.read(name)
                dims = read_image_dimensions(data)
                if dims is None:
                    logger.warning(
                        "Skipping unreadable embedded image %s in DOCX package.", name
                    )
                    continue
                width, height, ext = dims
                images.append(
                    ExtractedImage(
                        order=order.next(),
                        data=data,
                        ext=ext or filename_ext,
                        width=width,
                        height=height,
                        sha256=sha256_hex(data),
                    )
                )
        return images


def _iter_body(document: DocxDocument) -> Iterator[Paragraph | Table]:
    """Yield paragraphs and tables from the document body in document order.

    python-docx has no built-in mixed iterator, so we walk the body XML and wrap
    each child element in its corresponding proxy type.
    """
    body = document.element.body
    for child in body.iterchildren():
        if isinstance(child, CT_P):
            yield Paragraph(child, document)
        elif isinstance(child, CT_Tbl):
            yield Table(child, document)


def _heading_level(paragraph: Paragraph) -> int | None:
    """Return a 0-based heading depth for heading-styled paragraphs, else None.

    "Title" maps to depth 0; "Heading 1".."Heading 9" map to depths 0..8, so a
    Heading 1 sits under a Title in the section path.
    """
    style = paragraph.style
    name = (style.name if style is not None else "") or ""
    if name == "Title":
        return 0
    if name.startswith("Heading"):
        suffix = name[len("Heading") :].strip()
        if suffix.isdigit():
            return int(suffix) - 1
        return 0
    return None


def _section_path(heading_stack: list[str]) -> str | None:
    return " > ".join(heading_stack) if heading_stack else None


def _table_to_text(table: Table) -> str:
    rows: list[str] = []
    for row in table.rows:
        cells = [cell.text.strip() for cell in row.cells]
        if any(cells):
            rows.append(" | ".join(cells))
    return "\n".join(rows)


def _log_summary(result: ExtractionResult) -> None:
    logger.info(
        "Extracted docx: %d blocks, %d images",
        len(result.blocks),
        len(result.images),
    )
