"""WeasyPrint-backed PdfRenderer (S-2): Markdown -> HTML -> styled PDF bytes.

Pure rendering — no models, services, or storage. The Markdown from S-1 stays the
source of truth; this produces the PDF rendition. WeasyPrint is imported lazily (inside
``_html_to_pdf``) so the module imports — and ``build_html`` runs — without the system
libraries (pango/cairo/…) present; only the actual PDF conversion needs them.
"""

from __future__ import annotations

import logging

import markdown as markdown_lib

from app.application.ports.pdf_renderer import PdfRenderer, SummaryPdfMetadata
from app.infrastructure.rendering.summary_template import build_html

logger = logging.getLogger("knowledge.rendering")

# Markdown extensions: "extra" enables tables, fenced code, and definition lists;
# "sane_lists" keeps bullet/number lists well-behaved. Enough for the S-1 structure.
_MARKDOWN_EXTENSIONS = ["extra", "sane_lists"]

# Shown when the summary Markdown is empty/blank, so we still emit a valid, non-empty
# PDF instead of an unreadable blank page.
_EMPTY_BODY_HTML = "<p><em>No summary content was provided.</em></p>"


class RenderingError(RuntimeError):
    """Raised when a Markdown summary cannot be rendered to PDF.

    Distinct, catchable type so a later phase (S-3) can mark the summary Failed without
    the rendering internals leaking out or crashing the caller.
    """


def weasyprint_available() -> bool:
    """True if WeasyPrint and its system libraries can actually render a PDF."""
    try:
        import weasyprint  # noqa: F401

        weasyprint.HTML(string="<p>probe</p>").write_pdf()
        return True
    except Exception:  # ImportError, or missing pango/cairo/gobject at runtime
        return False


def markdown_to_html(markdown_text: str) -> str:
    """Convert summary Markdown to an HTML fragment (no WeasyPrint needed)."""
    rendered = markdown_lib.markdown(markdown_text or "", extensions=_MARKDOWN_EXTENSIONS)
    return rendered.strip() or _EMPTY_BODY_HTML


class WeasyPrintPdfRenderer(PdfRenderer):
    """PdfRenderer that renders the summary Markdown to a clean, report-quality PDF."""

    def to_html(self, markdown: str, metadata: SummaryPdfMetadata) -> str:
        """Full styled HTML document for ``markdown`` — the pre-PDF step (testable)."""
        return build_html(markdown_to_html(markdown), metadata)

    def render(self, markdown: str, metadata: SummaryPdfMetadata) -> bytes:
        try:
            html = self.to_html(markdown, metadata)
            pdf_bytes = self._html_to_pdf(html)
        except RenderingError:
            raise
        except Exception as exc:  # noqa: BLE001 — normalize every failure for S-3
            raise RenderingError(f"Failed to render summary PDF: {exc}") from exc

        if not pdf_bytes:
            raise RenderingError("PDF rendering produced no output.")
        return pdf_bytes

    @staticmethod
    def _html_to_pdf(html: str) -> bytes:
        import weasyprint  # lazy: the system libs are only needed here

        return weasyprint.HTML(string=html).write_pdf()
