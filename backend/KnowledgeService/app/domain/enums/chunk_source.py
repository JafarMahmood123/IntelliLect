from enum import Enum


class ChunkSource(str, Enum):
    """Origin of a chunk's text within its source document.

    TEXT   -> text extracted directly from the file's text layer.
    OCR    -> text recovered via OCR from an image/scanned page.
    MIXED  -> a chunk that merges both native (text-layer) and OCR content.
    CAPTION-> generated caption/description (e.g. for figures). Reserved for later.
    """

    TEXT = "Text"
    OCR = "Ocr"
    MIXED = "Mixed"
    CAPTION = "Caption"
