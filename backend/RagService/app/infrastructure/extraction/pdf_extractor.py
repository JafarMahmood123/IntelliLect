from __future__ import annotations

import pymupdf

from app.application.ports.extractor import CorruptFileError, Extractor
from app.domain.extraction.extracted_image import ExtractedImage
from app.domain.extraction.extraction_result import ExtractionResult
from app.domain.extraction.text_block import TextBlock, TextBlockKind
from app.infrastructure.extraction._support import (
    PDF_CONTENT_TYPES,
    PDF_EXTENSIONS,
    OrderCounter,
    logger,
    normalize_content_type,
    sha256_hex,
)

# A page whose text layer strips down to fewer than this many non-whitespace
# characters is treated as having no usable text (a likely scanned page).
_MIN_TEXT_CHARS = 3
# Image-to-page area ratio above which an image is considered to cover the page.
_COVERS_PAGE_RATIO = 0.8


class PdfExtractor(Extractor):
    """Extractor for PDF files, backed by PyMuPDF (fitz).

    Emits one PARAGRAPH block per non-empty text block in reading order, records
    pages whose text layer is empty/trivial, and inventories embedded images with
    a `covers_page` hint for full-page (scanned) images.
    """

    content_types = PDF_CONTENT_TYPES
    extensions = PDF_EXTENSIONS

    def supports(self, content_type: str) -> bool:
        return normalize_content_type(content_type) in self.content_types

    def extract(self, file_bytes: bytes, file_name: str) -> ExtractionResult:
        try:
            doc = pymupdf.open(stream=file_bytes, filetype="pdf")
        except Exception as exc:  # pymupdf raises FileDataError / RuntimeError
            raise CorruptFileError(
                f"Could not open PDF {file_name!r}: {exc}"
            ) from exc

        try:
            if doc.needs_pass:
                raise CorruptFileError(
                    f"PDF {file_name!r} is encrypted/password-protected and cannot "
                    f"be extracted."
                )
            return self._extract_document(doc)
        finally:
            doc.close()

    def _extract_document(self, doc: pymupdf.Document) -> ExtractionResult:
        order = OrderCounter()
        blocks: list[TextBlock] = []
        images: list[ExtractedImage] = []
        pages_without_text: list[int] = []

        for page_index in range(doc.page_count):
            page = doc[page_index]
            page_number = page_index + 1

            if self._page_text_is_trivial(page):
                pages_without_text.append(page_number)

            blocks.extend(self._text_blocks(page, page_number, order))
            images.extend(self._images(doc, page, page_number, order))

        result = ExtractionResult(
            source_format="pdf",
            blocks=blocks,
            images=images,
            page_count=doc.page_count,
            pages_without_text=pages_without_text,
        )
        _log_summary(result)
        return result

    @staticmethod
    def _page_text_is_trivial(page: pymupdf.Page) -> bool:
        stripped = "".join(page.get_text("text").split())
        return len(stripped) < _MIN_TEXT_CHARS

    @staticmethod
    def _text_blocks(
        page: pymupdf.Page, page_number: int, order: OrderCounter
    ) -> list[TextBlock]:
        blocks: list[TextBlock] = []
        # `sort=True` orders blocks top-to-bottom, left-to-right (reading order).
        for block in page.get_text("blocks", sort=True):
            # blocks() tuple: (x0, y0, x1, y1, text, block_no, block_type)
            block_type = block[6]
            if block_type != 0:  # 0 == text block, 1 == image block
                continue
            text = block[4].strip()
            if not text:
                continue
            blocks.append(
                TextBlock(
                    order=order.next(),
                    text=text,
                    kind=TextBlockKind.PARAGRAPH,
                    page=page_number,
                )
            )
        return blocks

    @staticmethod
    def _images(
        doc: pymupdf.Document,
        page: pymupdf.Page,
        page_number: int,
        order: OrderCounter,
    ) -> list[ExtractedImage]:
        images: list[ExtractedImage] = []
        page_area = abs(page.rect.get_area())

        for image_info in page.get_images(full=True):
            xref = image_info[0]
            try:
                extracted = doc.extract_image(xref)
            except Exception as exc:  # malformed image stream; skip, keep going
                logger.warning(
                    "Skipping unreadable image xref=%s on PDF page %d: %s",
                    xref,
                    page_number,
                    exc,
                )
                continue
            data = extracted.get("image")
            if not data:
                continue

            ext = (extracted.get("ext") or "").lower()
            width = int(extracted.get("width") or 0)
            height = int(extracted.get("height") or 0)

            images.append(
                ExtractedImage(
                    order=order.next(),
                    data=data,
                    ext=ext,
                    width=width,
                    height=height,
                    sha256=sha256_hex(data),
                    page=page_number,
                    covers_page=_image_covers_page(page, xref, page_area),
                )
            )
        return images


def _image_covers_page(page: pymupdf.Page, xref: int, page_area: float) -> bool:
    """True if the image's placement rect spans (nearly) the whole page area."""
    if page_area <= 0:
        return False
    try:
        rects = page.get_image_rects(xref)
    except Exception:  # placement info unavailable; treat as not covering
        return False
    for rect in rects:
        if abs(rect.get_area()) / page_area >= _COVERS_PAGE_RATIO:
            return True
    return False


def _log_summary(result: ExtractionResult) -> None:
    logger.info(
        "Extracted pdf: %d blocks, %d images, %d page(s), %d page(s) without text",
        len(result.blocks),
        len(result.images),
        result.page_count or 0,
        len(result.pages_without_text),
    )
