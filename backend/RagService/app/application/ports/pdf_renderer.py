from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import date, datetime


@dataclass(frozen=True)
class SummaryPdfMetadata:
    """Header/footer metadata for a rendered summary PDF (S-2).

    Framework-free value object. ``title`` heads the document; ``session_date`` and
    ``classroom_name`` populate the subheader when available; ``generated_at`` stamps
    the footer. None fields are simply omitted from the rendition.
    """

    title: str = "Session Summary"
    session_date: date | None = None
    classroom_name: str | None = None
    generated_at: datetime | None = None


class PdfRenderer(ABC):
    """Port that renders a Markdown summary into a styled PDF document (S-2).

    Implemented in the infrastructure layer (WeasyPrint). Pure rendering: the Markdown
    (from S-1) stays the source of truth and is consumed as-is — no models, services,
    or storage here. Implementations must raise a catchable rendering error on failure
    rather than crashing the caller (S-3 treats that as a failed summary).
    """

    @abstractmethod
    def render(self, markdown: str, metadata: SummaryPdfMetadata) -> bytes:
        """Return the PDF rendition of ``markdown`` as bytes (a valid, >=1-page PDF)."""
        raise NotImplementedError
