from __future__ import annotations

from uuid import uuid4

from fastapi.testclient import TestClient

from app.api.dependencies import get_chunk_repository, get_document_repository
from app.api.main import create_app
from app.domain.entities.chunk import Chunk
from app.domain.entities.document import Document
from app.domain.enums.chunk_source import ChunkSource
from app.domain.enums.document_status import DocumentStatus

from tests.ingestion.fakes import InMemoryChunkRepository, InMemoryDocumentRepository

HEADERS = {"X-Internal-Secret": "test-internal-secret"}


def _document(classroom_id, file_id) -> Document:
    return Document(
        classroom_id=classroom_id,
        file_id=file_id,
        s3_key="classrooms/x/doc.pdf",
        file_name="doc.pdf",
        content_type="application/pdf",
        status=DocumentStatus.DONE,
    )


def _chunk(document_id, classroom_id) -> Chunk:
    return Chunk(
        document_id=document_id,
        classroom_id=classroom_id,
        chunk_index=0,
        text="body",
        token_count=2,
        metadata={},
        source=ChunkSource.TEXT,
    )


def test_delete_classroom_index_removes_only_that_classrooms_data() -> None:
    app = create_app()
    documents = InMemoryDocumentRepository()
    chunks = InMemoryChunkRepository()

    target = uuid4()
    other = uuid4()

    # Two documents + their chunks in the target classroom, one in another classroom.
    doc_a = _document(target, uuid4())
    doc_b = _document(target, uuid4())
    doc_other = _document(other, uuid4())
    documents.seed(doc_a)
    documents.seed(doc_b)
    documents.seed(doc_other)

    import anyio

    async def _seed_chunks() -> None:
        await chunks.add_many([_chunk(doc_a.id, target)], [[0.0]])
        await chunks.add_many([_chunk(doc_b.id, target)], [[0.0]])
        await chunks.add_many([_chunk(doc_other.id, other)], [[0.0]])

    anyio.run(_seed_chunks)

    app.dependency_overrides[get_document_repository] = lambda: documents
    app.dependency_overrides[get_chunk_repository] = lambda: chunks

    with TestClient(app) as client:
        response = client.delete(
            f"/api/internal/documents/classrooms/{target}", headers=HEADERS
        )

    assert response.status_code == 200
    body = response.json()
    assert body["classroomId"] == str(target)
    assert body["documentsDeleted"] == 2
    assert body["chunksDeleted"] == 2

    # The other classroom's data is untouched.
    assert doc_other.id in chunks.by_document


def test_delete_classroom_index_is_idempotent() -> None:
    app = create_app()
    documents = InMemoryDocumentRepository()
    chunks = InMemoryChunkRepository()
    app.dependency_overrides[get_document_repository] = lambda: documents
    app.dependency_overrides[get_chunk_repository] = lambda: chunks

    with TestClient(app) as client:
        response = client.delete(
            f"/api/internal/documents/classrooms/{uuid4()}", headers=HEADERS
        )

    # A second/first pass over an already-clean classroom removes nothing and still succeeds.
    assert response.status_code == 200
    body = response.json()
    assert body["documentsDeleted"] == 0
    assert body["chunksDeleted"] == 0


def test_delete_classroom_index_requires_internal_secret() -> None:
    app = create_app()
    app.dependency_overrides[get_document_repository] = lambda: InMemoryDocumentRepository()
    app.dependency_overrides[get_chunk_repository] = lambda: InMemoryChunkRepository()

    with TestClient(app) as client:
        response = client.delete(f"/api/internal/documents/classrooms/{uuid4()}")

    assert response.status_code == 401
