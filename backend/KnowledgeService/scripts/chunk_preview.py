"""Eyeball chunking on a real file — extraction (+ OCR) then chunking.

Usage (from the KnowledgeService directory):

    python scripts/chunk_preview.py path/to/file.pdf
    python scripts/chunk_preview.py path/to/deck.pptx --semantic

Runs Phase 2 extraction, Phase 3 OCR (if the tesseract binary is present), then
Phase 4 chunking, and prints each chunk (index, page/slide/section, source,
token_count, snippet). Defaults to the STRUCTURAL strategy so it runs fully offline.

`--semantic` selects the semantic strategy, which calls the embedding model to find
topic breakpoints — that requires a running Ollama and is intended for later
validation, so it is NOT part of the offline default.
"""

from __future__ import annotations

import argparse
import asyncio
import sys
from pathlib import Path
from uuid import uuid4

from app.application.ports.extractor import ExtractionError
from app.domain.entities.chunk import Chunk
from app.infrastructure.chunking.factory import create_chunker
from app.infrastructure.config.settings import Settings, get_settings
from app.infrastructure.embeddings.ollama_embedding_provider import (
    OllamaEmbeddingError,
    OllamaEmbeddingProvider,
)
from app.infrastructure.extraction.router import ExtractorRouter
from app.infrastructure.ocr.tesseract_ocr_processor import (
    TesseractOcrProcessor,
    tesseract_available,
)

_CONTENT_TYPES = {
    ".pdf": "application/pdf",
    ".docx": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    ".pptx": "application/vnd.openxmlformats-officedocument.presentationml.presentation",
}


def _location(chunk: Chunk) -> str:
    meta = chunk.metadata
    if "page" in meta:
        return f"page {meta['page']}"
    if "slide" in meta:
        return f"slide {meta['slide']}"
    if "section" in meta:
        return f"[{meta['section']}]"
    return "-"


def _snippet(text: str, width: int = 100) -> str:
    collapsed = " ".join(text.split())
    return collapsed if len(collapsed) <= width else collapsed[: width - 1] + "…"


async def _run(path: Path, semantic: bool) -> int:
    content_type = _CONTENT_TYPES.get(path.suffix.lower())
    file_bytes = path.read_bytes()

    router = ExtractorRouter.default()
    try:
        result = router.extract(file_bytes, path.name, content_type)
    except ExtractionError as exc:
        print(f"Extraction failed: {exc}", file=sys.stderr)
        return 1

    if tesseract_available():
        result = await TesseractOcrProcessor(get_settings()).process(file_bytes, result)
    else:
        print("(tesseract not found — skipping OCR enrichment)", file=sys.stderr)

    strategy = "semantic" if semantic else "structural"
    settings = Settings(chunking_strategy=strategy)
    chunker = create_chunker(settings, OllamaEmbeddingProvider(settings))

    try:
        chunks = await chunker.chunk(result, uuid4(), uuid4())
    except OllamaEmbeddingError as exc:
        print(
            f"Semantic chunking needs a running Ollama and it is unreachable: {exc}",
            file=sys.stderr,
        )
        return 1

    print(f"File:      {path}")
    print(f"Format:    {result.source_format}")
    print(f"Strategy:  {strategy}")
    print(f"Chunks:    {len(chunks)}")
    print("\n--- Chunks (reading order) ---")
    if not chunks:
        print("  (none)")
    for chunk in chunks:
        print(
            f"  #{chunk.chunk_index:<3} {_location(chunk):<14} "
            f"{chunk.source.value:<6} {chunk.token_count:>4}tok  {_snippet(chunk.text)}"
        )
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Preview chunking of a document.")
    parser.add_argument("path", type=Path, help="Path to a .pdf, .docx, or .pptx file")
    parser.add_argument(
        "--semantic",
        action="store_true",
        help="Use the semantic strategy (requires a running Ollama; for later validation)",
    )
    args = parser.parse_args(argv)

    path: Path = args.path
    if not path.is_file():
        print(f"No such file: {path}", file=sys.stderr)
        return 2

    if args.semantic:
        print(
            "[semantic] Semantic chunking calls the embedding model to detect topic "
            "breakpoints. It requires a running Ollama and is intended for later "
            "validation — the offline default is structural.",
            file=sys.stderr,
        )

    return asyncio.run(_run(path, args.semantic))


if __name__ == "__main__":
    raise SystemExit(main())
