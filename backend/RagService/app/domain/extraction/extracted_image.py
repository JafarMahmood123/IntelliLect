from __future__ import annotations

from dataclasses import dataclass


@dataclass
class ExtractedImage:
    """An image embedded in a document, captured as raw bytes plus metadata.

    Pure domain object. `sha256` is the content hash so a later phase can dedupe
    the same image reused across pages/slides. `covers_page` is a PDF-only hint
    that the image spans (nearly) the whole page — i.e. a likely scanned page.
    """

    order: int  # global reading order across the whole document
    data: bytes
    ext: str  # normalized image subtype, e.g. "png", "jpeg"
    width: int
    height: int
    sha256: str  # hex digest of `data`
    page: int | None = None  # 1-based for PDF
    slide: int | None = None  # 1-based for PPTX
    covers_page: bool = False  # PDF only: image area ~= page area
