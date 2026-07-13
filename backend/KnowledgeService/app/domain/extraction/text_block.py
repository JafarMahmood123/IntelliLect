from __future__ import annotations

from dataclasses import dataclass
from enum import Enum


class TextBlockKind(str, Enum):
    """Structural role of a text block within its source document.

    A coarse, format-agnostic hint used by later phases (chunking) to keep titles
    and headings with the text they introduce and to treat tables/notes sensibly.
    """

    TITLE = "Title"
    HEADING = "Heading"
    PARAGRAPH = "Paragraph"
    TABLE = "Table"
    NOTES = "Notes"
    OTHER = "Other"


@dataclass
class TextBlock:
    """A single ordered piece of text extracted from a document.

    Pure domain object: no framework/parser imports. Location fields are mutually
    exclusive by format — PDF sets `page`, PPTX sets `slide`, DOCX sets `section`.
    """

    order: int  # global reading order across the whole document
    text: str
    kind: TextBlockKind
    page: int | None = None  # 1-based for PDF
    slide: int | None = None  # 1-based for PPTX
    section: str | None = None  # nearest heading path for DOCX, e.g. "Intro > Goals"
