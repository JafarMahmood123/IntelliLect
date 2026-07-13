from __future__ import annotations

from uuid import uuid4

from fastapi.testclient import TestClient

from app.api.dependencies import get_document_repository, get_ingestion_worker
from app.api.main import create_app

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
