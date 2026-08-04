from __future__ import annotations

from abc import ABC, abstractmethod

from app.domain.extraction.extraction_result import ExtractionResult


class OcrProcessor(ABC):
    """Port that enriches an ExtractionResult with OCR-recovered text.

    Implemented in the infrastructure layer (Tesseract). Given the original file
    bytes and the Phase 2 ExtractionResult, it OCRs only what needs it — scanned
    PDF pages with no text layer and text-bearing embedded images — and returns
    the result with the extra `source=OCR` blocks added. Native blocks are left
    untouched; implementations do no I/O beyond the passed-in bytes.
    """

    @abstractmethod
    async def process(
        self, file_bytes: bytes, result: ExtractionResult
    ) -> ExtractionResult:
        """Return `result` enriched with OCR text blocks (may be the same object)."""
        raise NotImplementedError
