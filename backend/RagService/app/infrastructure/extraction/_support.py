from __future__ import annotations

import hashlib
import io
import logging
import zipfile

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


# Magic numbers, longest-first where one is a prefix of another. Only formats worth
# NAMING appear here: the point is to tell a user what they actually uploaded, not to
# build a general file-type database.
_MAGIC: tuple[tuple[bytes, str], ...] = (
    (b"%PDF-", "pdf"),
    (b"\x89PNG\r\n\x1a\n", "png"),
    (b"\xff\xd8\xff", "jpeg"),
    (b"GIF87a", "gif"),
    (b"GIF89a", "gif"),
    (b"\x1f\x8b", "gzip"),
    # Compound File Binary: the pre-2007 Office container, so .doc/.ppt/.xls.
    (b"\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1", "ole2"),
)

_ZIP_MAGIC = (b"PK\x03\x04", b"PK\x05\x06", b"PK\x07\x08")

# What the OOXML formats put at the root of their zip.
_OOXML_MARKERS: tuple[tuple[str, str], ...] = (
    ("word/", "docx"),
    ("ppt/", "pptx"),
    ("xl/", "xlsx"),
)


def sniff_format(file_bytes: bytes) -> str | None:
    """Name the format the BYTES are, independent of what anyone called the file.

    Returns a canonical extension ("pdf", "docx", "png", "ole2", ...) or None when the
    bytes carry no signature worth trusting — which is the normal case for plain text and
    Markdown, and why an unrecognised result must never be treated as an error.

    This exists because neither of the two things we are told is reliable. The content type
    comes from the browser, which guesses from the extension; the extension comes from
    whoever named the file. When both are wrong together the parsers do not necessarily
    complain: PyMuPDF opens a PNG as a zero-page document and reports success, and the text
    decoder turns a PDF into paragraphs of replacement characters. Neither raises, so
    without this the file is indexed as empty or as noise and nobody is told.
    """
    for magic, name in _MAGIC:
        if file_bytes.startswith(magic):
            return name

    if any(file_bytes.startswith(magic) for magic in _ZIP_MAGIC):
        return _sniff_zip(file_bytes)

    return None


def _sniff_zip(file_bytes: bytes) -> str:
    """Tell the OOXML formats apart, all of which are zips, by what is inside."""
    try:
        with zipfile.ZipFile(io.BytesIO(file_bytes)) as archive:
            names = archive.namelist()
    except (zipfile.BadZipFile, OSError, ValueError):
        # A truncated or damaged zip. Still a zip as far as the user is concerned, and
        # saying so is more use than "unknown".
        return "zip"

    for prefix, name in _OOXML_MARKERS:
        if any(entry.startswith(prefix) for entry in names):
            return name

    return "zip"


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
