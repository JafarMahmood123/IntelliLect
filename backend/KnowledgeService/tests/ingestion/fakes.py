"""Offline fakes + fixtures for the ingestion tests.

No S3, no Ollama, no Postgres: file bytes come from an in-memory map, embeddings
are deterministic vectors of length EMBEDDING_DIM, and the repositories are plain
dicts. The extractor/OCR/chunker used in the tests are the REAL ones (they run
offline; OCR needs only the tesseract binary).
"""

from __future__ import annotations

import hashlib
from dataclasses import replace
from uuid import UUID, uuid4

import pymupdf

from app.application.ports.chunk_repository import ChunkRepository
from app.application.ports.document_repository import DocumentRepository
from app.application.ports.embedding_provider import EmbeddingProvider
from app.application.ports.file_storage import FileStorage
from app.application.services.ingestion_service import IngestionJob
from app.domain.entities.chunk import Chunk
from app.domain.entities.document import Document
from app.domain.enums.document_status import DocumentStatus


def make_pdf_bytes(variant: int = 1) -> bytes:
    """A deterministic 2-page text PDF. Different `variant` -> different bytes/hash."""
    document = pymupdf.open()
    for page_number in range(1, 3):
        page = document.new_page()
        page.insert_text((72, 100), f"Variant {variant} Chapter {page_number}")
        page.insert_text(
            (72, 140),
            f"This is body text for variant {variant}, page {page_number}. " * 3,
        )
    data = document.tobytes()
    document.close()
    return data


def make_job(
    *,
    file_id: UUID | None = None,
    classroom_id: UUID | None = None,
    s3_key: str = "classroom/doc.pdf",
    file_name: str = "doc.pdf",
    content_type: str = "application/pdf",
) -> IngestionJob:
    return IngestionJob(
        file_id=file_id or uuid4(),
        classroom_id=classroom_id or uuid4(),
        s3_key=s3_key,
        file_name=file_name,
        content_type=content_type,
    )


class FakeFileStorage(FileStorage):
    """Maps s3_key -> bytes. `put` lets a test change the content (re-index)."""

    def __init__(self, mapping: dict[str, bytes] | None = None) -> None:
        self._mapping: dict[str, bytes] = dict(mapping or {})
        self.calls = 0

    def put(self, s3_key: str, data: bytes) -> None:
        self._mapping[s3_key] = data

    async def get_bytes(self, s3_key: str) -> bytes:
        self.calls += 1
        return self._mapping[s3_key]


class FakeEmbeddingProvider(EmbeddingProvider):
    """Deterministic embeddings of length `dim`. Counts calls / records texts."""

    def __init__(self, dim: int) -> None:
        self._dim = dim
        self.embed_documents_calls = 0
        self.embedded_texts: list[str] = []

    async def embed_documents(self, texts: list[str]) -> list[list[float]]:
        self.embed_documents_calls += 1
        self.embedded_texts.extend(texts)
        return [self._vector(text) for text in texts]

    async def embed_query(self, text: str) -> list[float]:
        return self._vector(text)

    def _vector(self, text: str) -> list[float]:
        digest = hashlib.sha256(text.encode("utf-8")).digest()
        return [digest[i % len(digest)] / 255.0 for i in range(self._dim)]


class RaisingEmbeddingProvider(FakeEmbeddingProvider):
    """Fails on embed to exercise the failure path."""

    async def embed_documents(self, texts: list[str]) -> list[list[float]]:
        raise RuntimeError("embedder is down")


class InMemoryDocumentRepository(DocumentRepository):
    """Dict-backed DocumentRepository that mirrors the upsert-on-file_id semantics
    and records every status it transitions through (for assertions)."""

    def __init__(self) -> None:
        self._by_file_id: dict[UUID, Document] = {}
        self.status_history: dict[UUID, list[DocumentStatus]] = {}

    async def add(self, document: Document) -> Document:
        existing = self._by_file_id.get(document.file_id)
        if existing is not None:
            document.id = existing.id
            document.created_at_utc = existing.created_at_utc
        stored = replace(document)
        self._by_file_id[document.file_id] = stored
        self.status_history.setdefault(document.file_id, []).append(stored.status)
        return replace(stored)

    async def get_by_file_id(self, file_id: UUID) -> Document | None:
        stored = self._by_file_id.get(file_id)
        return replace(stored) if stored is not None else None

    async def update_status(
        self, file_id: UUID, status: DocumentStatus, error: str | None = None
    ) -> None:
        stored = self._by_file_id.get(file_id)
        if stored is None:
            return
        stored.status = status
        stored.error = error
        self.status_history.setdefault(file_id, []).append(status)

    async def delete_by_file_id(self, file_id: UUID) -> bool:
        return self._by_file_id.pop(file_id, None) is not None


class InMemoryChunkRepository(ChunkRepository):
    """Dict-backed ChunkRepository keyed by document_id, holding (chunk, embedding)."""

    def __init__(self) -> None:
        self.by_document: dict[UUID, list[tuple[Chunk, list[float]]]] = {}
        self.add_many_calls = 0
        self.delete_calls = 0

    async def add_many(self, chunks, embeddings) -> None:
        self.add_many_calls += 1
        for chunk, embedding in zip(chunks, embeddings, strict=True):
            self.by_document.setdefault(chunk.document_id, []).append(
                (chunk, list(embedding))
            )

    async def delete_by_document_id(self, document_id: UUID) -> int:
        removed = self.by_document.pop(document_id, [])
        self.delete_calls += 1
        return len(removed)

    async def search(self, classroom_id, query_embedding, top_k):
        # Not exercised by the ingestion tests; present to satisfy the port.
        return []
