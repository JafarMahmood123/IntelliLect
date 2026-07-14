from __future__ import annotations

from abc import ABC, abstractmethod

from app.domain.extraction.extraction_result import ExtractionResult


class ExtractionError(Exception):
    """Base class for all extraction-layer failures."""


class UnsupportedFormatError(ExtractionError):
    """The file's content type / extension is not one we can extract."""


class CorruptFileError(ExtractionError):
    """The file matched a supported format but could not be parsed.

    Covers truncated/corrupt containers and encrypted files we cannot open.
    """


class Extractor(ABC):
    """Port that turns raw file bytes into a normalized ExtractionResult.

    Implemented in the infrastructure layer (one per format, plus a router). The
    application/domain layers depend only on this abstraction. Implementations do
    no I/O beyond parsing the bytes they are handed.
    """

    @abstractmethod
    def supports(self, content_type: str) -> bool:
        """Return True if this extractor can handle the given content type."""
        raise NotImplementedError

    @abstractmethod
    def extract(self, file_bytes: bytes, file_name: str) -> ExtractionResult:
        """Parse `file_bytes` into an ExtractionResult.

        `file_name` is used only as a fallback hint (e.g. extension) and for log
        messages — never for I/O.

        Raises:
            UnsupportedFormatError: the input is not a format this extractor handles.
            CorruptFileError: the input is the right format but cannot be parsed.
        """
        raise NotImplementedError
