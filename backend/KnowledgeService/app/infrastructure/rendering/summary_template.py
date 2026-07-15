"""HTML template + stylesheet for the summary PDF (S-2).

Kept separate from the renderer so the look is easy to tweak without touching the
Markdown->HTML->PDF wiring. English, left-to-right (Arabic RTL is deferred). The CSS
is a Python string (rather than a packaged .css asset) so it ships with the module and
needs no package-data configuration. ``@page`` margin boxes provide the running footer
(page numbers + a "generated on" line) that WeasyPrint fills per page.

The document HTML is assembled with f-strings (not str.format / str %), so braces in
the CSS or in the Markdown-derived body are never re-interpreted. The only dynamic bit
of the CSS is the footer text, injected via a sentinel so the CSS keeps natural braces.
"""

from __future__ import annotations

import html as _html

_GENERATED_FOOTER_SENTINEL = "__GENERATED_FOOTER__"

SUMMARY_CSS = """
@page {
    size: A4;
    margin: 2.2cm 2cm 2cm 2cm;
    @bottom-left {
        content: "__GENERATED_FOOTER__";
        font-family: "DejaVu Sans", "Helvetica Neue", Arial, sans-serif;
        font-size: 8pt;
        color: #8a8f98;
    }
    @bottom-right {
        content: "Page " counter(page) " of " counter(pages);
        font-family: "DejaVu Sans", "Helvetica Neue", Arial, sans-serif;
        font-size: 8pt;
        color: #8a8f98;
    }
}

html {
    font-family: "DejaVu Sans", "Helvetica Neue", Arial, sans-serif;
    font-size: 11pt;
    line-height: 1.55;
    color: #23272f;
}

body { margin: 0; }

.doc-header {
    border-bottom: 2px solid #2f6df6;
    padding-bottom: 0.5cm;
    margin-bottom: 0.7cm;
}

.doc-header h1.doc-title {
    font-size: 22pt;
    font-weight: 700;
    color: #1b2430;
    margin: 0 0 0.15cm 0;
    padding: 0;
    border: none;
}

.doc-header .doc-subheader {
    font-size: 10.5pt;
    color: #5b6270;
    margin: 0;
}

.doc-body h1 {
    font-size: 16pt;
    color: #1b2430;
    margin: 0.7cm 0 0.2cm 0;
}

.doc-body h2 {
    font-size: 13.5pt;
    color: #2f6df6;
    margin: 0.6cm 0 0.15cm 0;
    padding-bottom: 0.1cm;
    border-bottom: 1px solid #e6e8ec;
}

.doc-body h3 {
    font-size: 11.5pt;
    color: #3a4250;
    margin: 0.4cm 0 0.1cm 0;
}

.doc-body p { margin: 0.15cm 0; }

.doc-body ul, .doc-body ol {
    margin: 0.15cm 0 0.25cm 0;
    padding-left: 0.7cm;
}

.doc-body li { margin: 0.08cm 0; }

.doc-body strong { color: #1b2430; }

.doc-body code {
    font-family: "DejaVu Sans Mono", monospace;
    background: #f3f4f6;
    padding: 0 3px;
    border-radius: 3px;
    font-size: 9.5pt;
}

.doc-body table {
    border-collapse: collapse;
    width: 100%;
    margin: 0.3cm 0;
}

.doc-body th, .doc-body td {
    border: 1px solid #d7dae0;
    padding: 4px 8px;
    text-align: left;
    font-size: 10pt;
}

.doc-body th { background: #f3f4f6; }
"""


def _format_date(value) -> str | None:
    try:
        return value.strftime("%B %d, %Y")
    except AttributeError:
        return None


def _subheader_html(metadata) -> str:
    parts: list[str] = []
    if getattr(metadata, "classroom_name", None):
        parts.append(f"Classroom: {_html.escape(metadata.classroom_name)}")
    session_on = _format_date(getattr(metadata, "session_date", None))
    if session_on:
        parts.append(f"Session date: {_html.escape(session_on)}")
    if not parts:
        return ""
    return f'<p class="doc-subheader">{" &middot; ".join(parts)}</p>'


def _stylesheet(metadata) -> str:
    generated_on = _format_date(getattr(metadata, "generated_at", None))
    footer = _html.escape(f"Generated on {generated_on}") if generated_on else ""
    return SUMMARY_CSS.replace(_GENERATED_FOOTER_SENTINEL, footer)


def build_html(body_html: str, metadata) -> str:
    """Wrap the Markdown-derived ``body_html`` in the styled document template.

    Pure string assembly (no WeasyPrint), so the resulting HTML — including the header
    metadata and stylesheet — is unit-testable on its own. All metadata is HTML-escaped.
    """
    title = _html.escape(metadata.title or "Session Summary")
    css = _stylesheet(metadata)
    subheader = _subheader_html(metadata)
    return (
        '<!DOCTYPE html>\n'
        '<html lang="en" dir="ltr">\n'
        "<head>\n"
        '<meta charset="utf-8">\n'
        f"<style>{css}</style>\n"
        "</head>\n"
        "<body>\n"
        '<header class="doc-header">\n'
        f'<h1 class="doc-title">{title}</h1>\n'
        f"{subheader}\n"
        "</header>\n"
        '<main class="doc-body">\n'
        f"{body_html}\n"
        "</main>\n"
        "</body>\n"
        "</html>\n"
    )
