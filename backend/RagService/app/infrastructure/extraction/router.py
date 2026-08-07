from __future__ import annotations

from app.application.ports.extractor import Extractor, UnsupportedFormatError
from app.domain.extraction.extraction_result import ExtractionResult
from app.infrastructure.extraction._support import (
    extension_of,
    normalize_content_type,
    sniff_format,
)
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
        self._refuse_if_the_bytes_disagree(file_bytes, file_name, content_type, extractor)
        return extractor.extract(file_bytes, file_name)

    def _refuse_if_the_bytes_disagree(
        self,
        file_bytes: bytes,
        file_name: str,
        content_type: str | None,
        chosen: Extractor,
    ) -> None:
        """Stop when the file's own signature contradicts the extractor we picked.

        Both inputs to `_resolve` are hearsay: the content type comes from the browser,
        which guesses from the extension, and the extension comes from whoever named the
        file. When they are wrong together, nothing downstream necessarily objects —

          * PyMuPDF opens a PNG as a zero-page document, and `filetype="pdf"` does not stop
            it. Extraction "succeeds" with no blocks, the document is marked Done, and the
            teacher sees an indexed file the assistant can never retrieve from.
          * The text decoder turns a PDF into paragraphs of cp1252 replacement characters
            and indexes them, putting noise into the classroom's knowledge base — which
            degrades retrieval for every later question, not just this document.

        Neither raises. Silence is the failure mode, so this refuses instead.

        Only a RECOGNISED signature counts. Plain text and Markdown have none, so an
        unknown result always proceeds — this can refuse a file, never accept one it
        otherwise would not.
        """
        sniffed = sniff_format(file_bytes)
        if sniffed is None:
            return

        expected = self._by_extension.get(sniffed)
        if expected is chosen:
            return

        called_it = normalize_content_type(content_type) or extension_of(file_name) or "?"
        if expected is None:
            raise UnsupportedFormatError(
                f"{file_name!r} was sent as {called_it} but its contents are {sniffed}, "
                f"which this service cannot extract. Supported: PDF, DOCX, PPTX, TXT/MD."
            )

        raise UnsupportedFormatError(
            f"{file_name!r} was sent as {called_it} but its contents are {sniffed}. "
            f"Re-upload it with the right name and type."
        )

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
