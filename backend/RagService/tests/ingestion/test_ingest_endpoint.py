from __future__ import annotations

from uuid import uuid4

from fastapi.testclient import TestClient

from app.api.dependencies import get_document_repository, get_ingestion_worker
from app.api.main import create_app
from app.domain.entities.document import Document
from app.domain.enums.document_status import DocumentStatus

from tests.ingestion.fakes import InMemoryDocumentRepository

HEADERS = {"X-Internal-Secret": "test-internal-secret"}


def _payload() -> dict:
    return {
        "fileId": str(uuid4()),
        "classroomId": str(uuid4()),
        "s3Key": "classroom/doc.pdf",
        "fileName": "doc.pdf",
        "contentType": "application/pdf",
    }


class _OkWorker:
    def enqueue(self, job) -> bool:
        return True


class _FullWorker:
    def enqueue(self, job) -> bool:
        return False


def test_ingest_returns_202_when_enqueued_and_503_when_queue_full() -> None:
    app = create_app()
    documents = InMemoryDocumentRepository()
    app.dependency_overrides[get_document_repository] = lambda: documents
    app.dependency_overrides[get_ingestion_worker] = lambda: _OkWorker()

    with TestClient(app) as client:
        accepted = client.post(
            "/api/internal/documents/ingest", json=_payload(), headers=HEADERS
        )
        assert accepted.status_code == 202

        # A full queue surfaces as 503.
        app.dependency_overrides[get_ingestion_worker] = lambda: _FullWorker()
        rejected = client.post(
            "/api/internal/documents/ingest", json=_payload(), headers=HEADERS
        )
        assert rejected.status_code == 503


def test_ingest_requires_internal_secret() -> None:
    app = create_app()
    documents = InMemoryDocumentRepository()
    app.dependency_overrides[get_document_repository] = lambda: documents
    app.dependency_overrides[get_ingestion_worker] = lambda: _OkWorker()

    with TestClient(app) as client:
        unauthorized = client.post("/api/internal/documents/ingest", json=_payload())
        assert unauthorized.status_code == 401


def _seed_document(documents: InMemoryDocumentRepository, file_id) -> None:
    documents.seed(
        Document(
            classroom_id=uuid4(), file_id=file_id, s3_key="k", file_name="f.pdf",
            content_type="application/pdf", status=DocumentStatus.FAILED,
            last_error="boom", attempts=3,
        )
    )


def test_reindex_resets_and_enqueues_a_known_document() -> None:
    app = create_app()
    documents = InMemoryDocumentRepository()
    file_id = uuid4()
    _seed_document(documents, file_id)
    app.dependency_overrides[get_document_repository] = lambda: documents
    app.dependency_overrides[get_ingestion_worker] = lambda: _OkWorker()

    with TestClient(app) as client:
        response = client.post(
            f"/api/internal/documents/{file_id}/reindex", headers=HEADERS
        )

    assert response.status_code == 202
    assert response.json()["status"] == DocumentStatus.PENDING.value
    stored = documents._by_file_id[file_id]  # noqa: SLF001
    assert stored.status == DocumentStatus.PENDING
    assert stored.last_error is None
    assert stored.attempts == 0  # manual retry resets the attempt count


def test_reindex_unknown_document_returns_404() -> None:
    app = create_app()
    documents = InMemoryDocumentRepository()
    app.dependency_overrides[get_document_repository] = lambda: documents
    app.dependency_overrides[get_ingestion_worker] = lambda: _OkWorker()

    with TestClient(app) as client:
        response = client.post(
            f"/api/internal/documents/{uuid4()}/reindex", headers=HEADERS
        )

    assert response.status_code == 404


def test_reindex_requires_internal_secret() -> None:
    app = create_app()
    documents = InMemoryDocumentRepository()
    file_id = uuid4()
    _seed_document(documents, file_id)
    app.dependency_overrides[get_document_repository] = lambda: documents
    app.dependency_overrides[get_ingestion_worker] = lambda: _OkWorker()

    with TestClient(app) as client:
        response = client.post(f"/api/internal/documents/{file_id}/reindex")

    assert response.status_code == 401
