"""Run the FULL ingestion pipeline on a local file — LIVE, and DEFERRED.

This drives IngestionService with the REAL providers, except it reads bytes from a
local path instead of S3. It therefore REQUIRES:
  - Ollama running with the configured embedding model (qwen3-embedding), and
  - Postgres up with migrations applied (the chunks/documents tables + pgvector).

It is intended for end-to-end validation once the developer is back home with both
services running. It is NOT part of the offline test suite and will fail fast if
Ollama or Postgres is unreachable.

Usage (from the RagService directory, with .env pointing at a live DB/Ollama):

    python scripts/ingest_local.py path/to/file.pdf
    python scripts/ingest_local.py path/to/deck.pptx --classroom-id <uuid>
"""

from __future__ import annotations

import argparse
import asyncio
import sys
from pathlib import Path
from uuid import UUID, uuid4

from app.application.ports.file_storage import FileStorage
from app.application.services.ingestion_service import IngestionJob, IngestionService
from app.domain.entities.document import Document
from app.domain.enums.document_status import DocumentStatus
from app.infrastructure.chunking.factory import create_chunker
from app.infrastructure.config.settings import get_settings
from app.infrastructure.embeddings.ollama_embedding_provider import OllamaEmbeddingProvider
from app.infrastructure.extraction.router import ExtractorRouter
from app.infrastructure.ocr.tesseract_ocr_processor import TesseractOcrProcessor
from app.infrastructure.persistence.chunk_repository import SqlAlchemyChunkRepository
from app.infrastructure.persistence.database import dispose_engine, get_session_factory
from app.infrastructure.persistence.document_repository import SqlAlchemyDocumentRepository

_CONTENT_TYPES = {
    ".pdf": "application/pdf",
    ".docx": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    ".pptx": "application/vnd.openxmlformats-officedocument.presentationml.presentation",
}


class LocalFileStorage(FileStorage):
    """FileStorage that returns the given local file's bytes, ignoring the key."""

    def __init__(self, path: Path) -> None:
        self._path = path

    async def get_bytes(self, s3_key: str) -> bytes:
        return self._path.read_bytes()


async def _run(path: Path, classroom_id: UUID) -> int:
    settings = get_settings()
    content_type = _CONTENT_TYPES.get(path.suffix.lower(), "application/octet-stream")

    document = Document(
        classroom_id=classroom_id,
        file_id=uuid4(),
        s3_key=str(path),
        file_name=path.name,
        content_type=content_type,
        status=DocumentStatus.PENDING,
    )

    storage = LocalFileStorage(path)
    embedder = OllamaEmbeddingProvider(settings)
    chunker = create_chunker(settings, embedder)
    extractor = ExtractorRouter.default()
    ocr = TesseractOcrProcessor(settings)
    session_factory = get_session_factory()

    # Upsert the Pending row (its own transaction), then run ingestion.
    async with session_factory() as session:
        await SqlAlchemyDocumentRepository(session).add(document)
        await session.commit()

    async with session_factory() as session:
        service = IngestionService(
            file_storage=storage,
            extractor=extractor,
            ocr_processor=ocr,
            chunker=chunker,
            embedding_provider=embedder,
            document_repository=SqlAlchemyDocumentRepository(session),
            chunk_repository=SqlAlchemyChunkRepository(session),
            embed_batch_size=settings.embed_batch_size,
        )
        outcome = await service.ingest(IngestionJob.from_document(document))
        await session.commit()

    print(f"File:        {path}")
    print(f"Document id: {document.id}")
    print(f"Status:      {outcome.status.value}")
    print(f"Chunks:      {outcome.chunk_count}")
    if outcome.skipped:
        print("(skipped — already ingested with the same content hash)")
    if outcome.error:
        print(f"Error:       {outcome.error}", file=sys.stderr)
    return 0 if outcome.status == DocumentStatus.DONE else 1


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Live local ingestion (needs Ollama + Postgres).")
    parser.add_argument("path", type=Path, help="Path to a .pdf, .docx, or .pptx file")
    parser.add_argument(
        "--classroom-id",
        type=UUID,
        default=None,
        help="Classroom UUID to tag the document with (defaults to a random one)",
    )
    args = parser.parse_args(argv)

    if not args.path.is_file():
        print(f"No such file: {args.path}", file=sys.stderr)
        return 2

    classroom_id = args.classroom_id or uuid4()

    async def _main() -> int:
        try:
            return await _run(args.path, classroom_id)
        finally:
            await dispose_engine()

    return asyncio.run(_main())


if __name__ == "__main__":
    raise SystemExit(main())
