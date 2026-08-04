from __future__ import annotations

import asyncio
import io
import logging
import threading
from concurrent.futures import ThreadPoolExecutor

import pymupdf
import pytesseract
from PIL import Image

from app.application.ports.ocr_processor import OcrProcessor
from app.domain.extraction.extracted_image import ExtractedImage
from app.domain.extraction.extraction_result import ExtractionResult
from app.domain.extraction.text_block import TextBlock, TextBlockKind, TextBlockSource
from app.infrastructure.config.settings import Settings

logger = logging.getLogger("knowledge.ocr")


def tesseract_available() -> bool:
    """Return True if the tesseract binary is reachable on PATH."""
    try:
        pytesseract.get_tesseract_version()
        return True
    except Exception:  # TesseractNotFoundError and any probing failure
        return False


class TesseractOcrProcessor(OcrProcessor):
    """OcrProcessor backed by the local Tesseract binary (English only).

    Runs a cheap-first cascade over an ExtractionResult: scanned PDF pages that
    have no text layer are rasterized and OCR'd; text-bearing embedded images are
    size-gated, downscaled, and OCR'd. Low-confidence or trivially short output is
    dropped. Identical images (by sha256) are OCR'd once and reused per location.

    OCR calls run on a bounded ThreadPoolExecutor (`ocr_max_workers`) to cap RAM —
    pytesseract shells out to the tesseract binary, so threads give real parallelism.

    The `last_*` attributes expose stats from the most recent process() call (for
    the CLI preview and tests). They are not safe to read under concurrent
    process() calls on the same instance.
    """

    def __init__(self, settings: Settings) -> None:
        self._lang = settings.ocr_lang
        self._dpi = settings.ocr_dpi
        self._max_workers = max(1, settings.ocr_max_workers)
        self._min_image_px = settings.ocr_min_image_px
        self._max_image_px = settings.ocr_max_image_px
        self._min_confidence = settings.ocr_min_confidence
        self._min_chars = settings.ocr_min_chars

        self._lock = threading.Lock()
        self._invocations = 0
        self.last_ocr_invocations = 0
        self.last_cache_hits = 0
        self.last_confidence_by_order: dict[int, float] = {}

    async def process(
        self, file_bytes: bytes, result: ExtractionResult
    ) -> ExtractionResult:
        self._reset_stats()

        if not tesseract_available():
            logger.warning(
                "Tesseract binary not found on PATH; returning result without OCR "
                "enrichment. Install tesseract-ocr + tesseract-ocr-eng."
            )
            return result

        page_candidates = self._page_candidates(result)
        image_candidates = self._image_candidates(result)
        # Dedupe images by content hash — OCR each unique image once.
        unique_images: dict[str, ExtractedImage] = {}
        for image in image_candidates:
            unique_images.setdefault(image.sha256, image)

        if not page_candidates and not unique_images:
            return result

        loop = asyncio.get_running_loop()
        page_results: dict[int, tuple[str, float]] = {}
        image_results: dict[str, tuple[str, float]] = {}
        with ThreadPoolExecutor(
            max_workers=self._max_workers, thread_name_prefix="ocr"
        ) as pool:
            page_futures = {
                page: loop.run_in_executor(pool, self._ocr_pdf_page, file_bytes, page)
                for page in page_candidates
            }
            image_futures = {
                sha: loop.run_in_executor(pool, self._ocr_image_bytes, image.data)
                for sha, image in unique_images.items()
            }
            for page, future in page_futures.items():
                page_results[page] = await future
            for sha, future in image_futures.items():
                image_results[sha] = await future

        ocr_blocks, confidence_by_order = self._build_blocks(
            result, page_candidates, image_candidates, page_results, image_results
        )
        result.blocks.extend(ocr_blocks)

        self.last_ocr_invocations = self._invocations
        self.last_cache_hits = len(image_candidates) - len(unique_images)
        self.last_confidence_by_order = confidence_by_order
        logger.info(
            "OCR enrichment: %d block(s) added, %d tesseract call(s), %d cache hit(s).",
            len(ocr_blocks),
            self.last_ocr_invocations,
            self.last_cache_hits,
        )
        return result

    # --- Candidate selection --------------------------------------------------

    def _page_candidates(self, result: ExtractionResult) -> list[int]:
        """Scanned PDF pages (1-based) with no text layer, in ascending order."""
        if result.source_format != "pdf":
            return []
        return sorted(set(result.pages_without_text))

    def _image_candidates(self, result: ExtractionResult) -> list[ExtractedImage]:
        """Embedded images worth OCR'ing, in the result's original image order."""
        covered_pages = (
            set(result.pages_without_text) if result.source_format == "pdf" else set()
        )
        candidates: list[ExtractedImage] = []
        for image in result.images:
            # Size gate: skip decorative icons / bullets.
            if max(image.width, image.height) < self._min_image_px:
                continue
            # Skip a full-page PDF image already OCR'd as a scanned page (case 1).
            if image.covers_page and image.page in covered_pages:
                continue
            candidates.append(image)
        return candidates

    # --- Block assembly -------------------------------------------------------

    def _build_blocks(
        self,
        result: ExtractionResult,
        page_candidates: list[int],
        image_candidates: list[ExtractedImage],
        page_results: dict[int, tuple[str, float]],
        image_results: dict[str, tuple[str, float]],
    ) -> tuple[list[TextBlock], dict[int, float]]:
        # Continue the global reading order after the native blocks.
        next_order = max((block.order for block in result.blocks), default=-1) + 1
        blocks: list[TextBlock] = []
        confidence_by_order: dict[int, float] = {}

        for page in page_candidates:
            text, confidence = page_results.get(page, ("", 0.0))
            accepted = self._accepted_text(text, confidence)
            if accepted is None:
                continue
            blocks.append(
                TextBlock(
                    order=next_order,
                    text=accepted,
                    kind=TextBlockKind.PARAGRAPH,
                    page=page,
                    source=TextBlockSource.OCR,
                )
            )
            confidence_by_order[next_order] = confidence
            next_order += 1

        for image in image_candidates:
            text, confidence = image_results.get(image.sha256, ("", 0.0))
            accepted = self._accepted_text(text, confidence)
            if accepted is None:
                continue
            blocks.append(
                TextBlock(
                    order=next_order,
                    text=accepted,
                    kind=TextBlockKind.PARAGRAPH,
                    page=image.page,
                    slide=image.slide,
                    source=TextBlockSource.OCR,
                )
            )
            confidence_by_order[next_order] = confidence
            next_order += 1

        return blocks, confidence_by_order

    def _accepted_text(self, text: str, confidence: float) -> str | None:
        """Return the block text if it clears the confidence/length gates, else None."""
        cleaned = " ".join(text.split())
        if confidence < self._min_confidence:
            return None
        if len(cleaned) < self._min_chars:
            return None
        return text.strip()

    # --- OCR workers (run in the thread pool) ---------------------------------

    def _ocr_pdf_page(self, file_bytes: bytes, page_number: int) -> tuple[str, float]:
        """Rasterize a single PDF page at OCR_DPI and OCR it. Reopens the PDF per
        page so each worker thread has its own (non-thread-safe) Document."""
        try:
            doc = pymupdf.open(stream=file_bytes, filetype="pdf")
        except Exception as exc:
            logger.warning("OCR could not reopen PDF for page %d: %s", page_number, exc)
            return "", 0.0
        try:
            page = doc[page_number - 1]
            pixmap = page.get_pixmap(dpi=self._dpi)
            png_bytes = pixmap.tobytes("png")
            pixmap = None  # free the rendered buffer promptly
            with Image.open(io.BytesIO(png_bytes)) as image:
                image.load()
                return self._run_ocr(image)
        except Exception as exc:
            logger.warning("OCR failed for PDF page %d: %s", page_number, exc)
            return "", 0.0
        finally:
            doc.close()

    def _ocr_image_bytes(self, data: bytes) -> tuple[str, float]:
        """Downscale an embedded image to the max long edge and OCR it."""
        try:
            with Image.open(io.BytesIO(data)) as image:
                image.load()
                prepared = self._prepare_image(image)
                try:
                    return self._run_ocr(prepared)
                finally:
                    if prepared is not image:
                        prepared.close()
        except Exception as exc:
            logger.warning("OCR failed for embedded image: %s", exc)
            return "", 0.0

    def _prepare_image(self, image: Image.Image) -> Image.Image:
        """Coerce to a Tesseract-friendly mode and cap the long edge (RAM/speed)."""
        prepared = image
        if prepared.mode not in ("L", "RGB"):
            prepared = prepared.convert("RGB")
        long_edge = max(prepared.size)
        if long_edge > self._max_image_px:
            scale = self._max_image_px / long_edge
            new_size = (
                max(1, round(prepared.width * scale)),
                max(1, round(prepared.height * scale)),
            )
            resized = prepared.resize(new_size, Image.LANCZOS)
            if prepared is not image:
                prepared.close()
            prepared = resized
        return prepared

    def _run_ocr(self, image: Image.Image) -> tuple[str, float]:
        """Invoke Tesseract once and return (reconstructed_text, mean_confidence)."""
        with self._lock:
            self._invocations += 1
        data = pytesseract.image_to_data(
            image, lang=self._lang, output_type=pytesseract.Output.DICT
        )
        return _parse_ocr_data(data)

    def _reset_stats(self) -> None:
        with self._lock:
            self._invocations = 0
        self.last_ocr_invocations = 0
        self.last_cache_hits = 0
        self.last_confidence_by_order = {}


def _parse_ocr_data(data: dict) -> tuple[str, float]:
    """Reconstruct line-grouped text and mean word confidence from image_to_data.

    Confidence is averaged over entries with conf >= 0 (actual recognized tokens;
    Tesseract reports -1 for structural rows). Text is grouped by (block, par, line)
    so the block reads in natural order.
    """
    texts = data.get("text", [])
    confidences_raw = data.get("conf", [])
    block_nums = data.get("block_num", [])
    par_nums = data.get("par_num", [])
    line_nums = data.get("line_num", [])

    confidences: list[float] = []
    lines: dict[tuple[int, int, int], list[str]] = {}
    for index in range(len(texts)):
        try:
            confidence = float(confidences_raw[index])
        except (TypeError, ValueError, IndexError):
            confidence = -1.0
        if confidence < 0:
            continue
        confidences.append(confidence)
        word = (texts[index] or "").strip()
        if not word:
            continue
        key = (block_nums[index], par_nums[index], line_nums[index])
        lines.setdefault(key, []).append(word)

    text = "\n".join(" ".join(words) for _, words in sorted(lines.items()))
    mean_confidence = sum(confidences) / len(confidences) if confidences else 0.0
    return text.strip(), mean_confidence
