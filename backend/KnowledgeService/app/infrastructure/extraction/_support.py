from __future__ import annotations

import hashlib
import io
import logging

from PIL import Image, UnidentifiedImageError

logger = logging.getLogger("knowledge.extraction")

# Content types and extensions per source format. Shared by the concrete
# extractors (for `supports`) and by the router (for dispatch).
PDF_CONTENT_TYPES = frozenset({"application/pdf"})
DOCX_CONTENT_TYPES = frozenset(
    {"application/vnd.openxmlformats-officedocument.wordprocessingml.document"}
)
PPTX_CONTENT_TYPES = frozenset(
    {"application/vnd.openxmlformats-officedocument.presentationml.presentation"}
)
# Markdown lives here too: we extract it AS plain text (no heading parsing), so a
# separate extractor would differ only in its name.
TXT_CONTENT_TYPES = frozenset({"text/plain", "text/markdown", "text/x-markdown"})

PDF_EXTENSIONS = frozenset({"pdf"})
DOCX_EXTENSIONS = frozenset({"docx"})
PPTX_EXTENSIONS = frozenset({"pptx"})
TXT_EXTENSIONS = frozenset({"txt", "md", "markdown"})


class OrderCounter:
    """Monotonic counter for the global reading order shared by blocks and images.

    A single sequence spans both so their relative order is preserved in one space.
    """

    def __init__(self) -> None:
        self._next = 0

    def next(self) -> int:
        value = self._next
        self._next += 1
        return value


def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def normalize_content_type(content_type: str | None) -> str:
    """Lowercase and drop any parameters (e.g. `; charset=...`) from a MIME type."""
    if not content_type:
        return ""
    return content_type.split(";", 1)[0].strip().lower()


def extension_of(file_name: str) -> str:
    """Return the lowercase extension without the dot, or "" if none."""
    _, _, ext = file_name.rpartition(".")
    return ext.lower() if ext and ext != file_name else ""


def read_image_dimensions(data: bytes) -> tuple[int, int, str] | None:
    """Return (width, height, normalized_ext) for image bytes, or None if unreadable.

    Uses Pillow's decoded format (e.g. "JPEG" -> "jpeg") as the authoritative ext.
    Returns None for formats Pillow cannot identify (e.g. WMF/EMF vector art), so
    callers can skip them.
    """
    try:
        with Image.open(io.BytesIO(data)) as image:
            width, height = image.size
            fmt = (image.format or "").lower()
        return width, height, fmt
    except (UnidentifiedImageError, OSError, ValueError):
        return None
