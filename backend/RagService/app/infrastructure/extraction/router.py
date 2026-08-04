from __future__ import annotations

from app.application.ports.extractor import Extractor, UnsupportedFormatError
from app.domain.extraction.extraction_result import ExtractionResult
from app.infrastructure.extraction._support import extension_of, normalize_content_type
from app.infrastructure.extraction.docx_extractor import DocxExtractor
from app.infrastructure.extraction.pdf_extractor import PdfExtractor
from app.infrastructure.extraction.pptx_extractor import PptxExtractor
from app.infrastructure.extraction.txt_extractor import TxtExtractor


class ExtractorRouter(Extractor):
    """Dispatches extraction to the right per-format extractor.

    Chooses by content type first, then falls back to the file-name extension.
    Unknown formats raise UnsupportedFormatError. This is the single Extractor the
    rest of the app depends on.
    """

    def __init__(self, extractors: list[Extractor]) -> None:
        self._extractors = extractors
        self._by_content_type: dict[str, Extractor] = {}
        self._by_extension: dict[str, Extractor] = {}
        for extractor in extractors:
            for content_type in getattr(extractor, "content_types", ()):
                self._by_content_type[content_type] = extractor
            for extension in getattr(extractor, "extensions", ()):
                self._by_extension[extension] = extractor

    @classmethod
    def default(cls) -> ExtractorRouter:
        """Build a router wired with the PDF, DOCX, PPTX, and TXT extractors."""
        return cls([PdfExtractor(), DocxExtractor(), PptxExtractor(), TxtExtractor()])

    def supports(self, content_type: str) -> bool:
        return normalize_content_type(content_type) in self._by_content_type

    def extract(
        self,
        file_bytes: bytes,
        file_name: str,
        content_type: str | None = None,
    ) -> ExtractionResult:
        """Route to the extractor for `content_type`, falling back to the extension.

        `content_type` is optional so the router still satisfies the Extractor port
        (2-arg call); when omitted, dispatch is by the file-name extension alone.
        """
        extractor = self._resolve(content_type, file_name)
        return extractor.extract(file_bytes, file_name)

    def _resolve(self, content_type: str | None, file_name: str) -> Extractor:
        normalized = normalize_content_type(content_type)
        if normalized:
            extractor = self._by_content_type.get(normalized)
            if extractor is not None:
                return extractor

        extension = extension_of(file_name)
        extractor = self._by_extension.get(extension)
        if extractor is not None:
            return extractor

        raise UnsupportedFormatError(
            f"No extractor for content type {content_type!r} / file {file_name!r}. "
            f"Supported: PDF, DOCX, PPTX, TXT/MD."
        )
