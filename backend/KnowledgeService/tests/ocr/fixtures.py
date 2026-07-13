"""Programmatic fixtures for the OCR tests.

Images and PDFs are rendered in-code with Pillow + PyMuPDF so the suite carries no
binary fixtures. Pillow (>=10.1) bundles a scalable DejaVu Sans that
`ImageFont.load_default(size=...)` uses, giving text crisp enough for Tesseract.
"""

from __future__ import annotations

import hashlib
import io

import pymupdf
from PIL import Image, ImageDraw, ImageFont

from app.domain.extraction.extracted_image import ExtractedImage

# A sentence made of common words Tesseract reads reliably.
SENTENCE = "The quick brown fox jumps over the lazy dog"


def _font(size: int) -> ImageFont.FreeTypeFont:
    # Pillow's scalable bundled default (DejaVu Sans); avoids depending on system fonts.
    return ImageFont.load_default(size=size)


def make_text_image(
    text: str = SENTENCE,
    size: tuple[int, int] = (1000, 240),
    font_size: int = 56,
) -> bytes:
    """A white PNG with a single line of black text."""
    image = Image.new("RGB", size, "white")
    draw = ImageDraw.Draw(image)
    draw.text((40, 90), text, fill="black", font=_font(font_size))
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return buffer.getvalue()


def make_page_image(
    text: str = SENTENCE,
    size: tuple[int, int] = (1240, 1754),  # A4 aspect (~0.707), so no distortion
    font_size: int = 64,
) -> bytes:
    """A page-shaped white PNG with a sentence near the top — used as a scanned page."""
    image = Image.new("RGB", size, "white")
    draw = ImageDraw.Draw(image)
    draw.text((80, 140), text, fill="black", font=_font(font_size))
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return buffer.getvalue()


def make_icon_image(size: int = 48, color: tuple[int, int, int] = (10, 120, 200)) -> bytes:
    """A tiny solid PNG below OCR_MIN_IMAGE_PX (a decorative icon)."""
    buffer = io.BytesIO()
    Image.new("RGB", (size, size), color).save(buffer, format="PNG")
    return buffer.getvalue()


def make_noise_image(size: tuple[int, int] = (400, 400), seed: int = 1234) -> bytes:
    """A deterministic RGB-noise PNG with no real text (low-confidence OCR)."""
    # Deterministic pseudo-random pixels without importing random (linear congruential).
    width, height = size
    state = seed
    pixels = bytearray(width * height * 3)
    for i in range(len(pixels)):
        state = (1103515245 * state + 12345) & 0x7FFFFFFF
        pixels[i] = state & 0xFF
    image = Image.frombytes("RGB", size, bytes(pixels))
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return buffer.getvalue()


def make_scanned_pdf(text: str = SENTENCE) -> bytes:
    """A 1-page PDF whose only content is a full-page text image (no text layer)."""
    png = make_page_image(text)
    document = pymupdf.open()
    page = document.new_page()  # A4
    page.insert_image(page.rect, stream=png)
    data = document.tobytes()
    document.close()
    return data


def extracted_image(
    png: bytes,
    *,
    order: int = 0,
    page: int | None = None,
    slide: int | None = None,
    covers_page: bool = False,
) -> ExtractedImage:
    """Build an ExtractedImage from PNG bytes, deriving dims + sha256 like Phase 2."""
    with Image.open(io.BytesIO(png)) as image:
        width, height = image.size
        ext = (image.format or "png").lower()
    return ExtractedImage(
        order=order,
        data=png,
        ext=ext,
        width=width,
        height=height,
        sha256=hashlib.sha256(png).hexdigest(),
        page=page,
        slide=slide,
        covers_page=covers_page,
    )


def normalize(text: str) -> str:
    """Lowercase and collapse whitespace, for lenient OCR substring assertions."""
    return " ".join(text.split()).lower()
