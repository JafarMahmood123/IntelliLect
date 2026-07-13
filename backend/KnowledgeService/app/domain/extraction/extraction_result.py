from __future__ import annotations

from dataclasses import dataclass, field

from app.domain.extraction.extracted_image import ExtractedImage
from app.domain.extraction.text_block import TextBlock


@dataclass
class ExtractionResult:
    """The normalized, in-memory representation of one extracted document.

    Produced by the extraction layer from raw file bytes. Carries no infrastructure
    concerns — just ordered text blocks and an inventory of embedded images, plus a
    few document-level hints for later phases (chunking, OCR triage).
    """

    source_format: str  # "pdf" | "docx" | "pptx"
    blocks: list[TextBlock] = field(default_factory=list)
    images: list[ExtractedImage] = field(default_factory=list)
    page_count: int | None = None
    # PDF pages (1-based) whose text layer is empty/trivial — candidates for OCR.
    pages_without_text: list[int] = field(default_factory=list)
