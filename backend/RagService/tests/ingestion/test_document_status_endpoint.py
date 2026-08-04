from __future__ import annotations

from uuid import uuid4

from fastapi.testclient import TestClient

from app.api.dependencies import get_document_repository
from app.api.main import create_app
from app.domain.entities.document import Document
from app.domain.enums.document_status import DocumentStatus

from tests.ingestion.fakes import InMemoryDocumentRepository

HEADERS = {"X-Internal-Secret": "test-internal-secret"}


def _seed(documents: InMemoryDocumentRepository, file_id, status: DocumentStatus) -> None:
    documents.seed(
        Document(
            classroom_id=uuid4(),
            file_id=file_id,
            s3_key="classrooms/x/doc.pdf",
            file_name="doc.pdf",
            content_type="application/pdf",
            status=status,
        )
    )


def test_status_returns_lifecycle_state_for_a_known_document() -> None:
    app = create_app()
    documents = InMemoryDocumentRepository()
    file_id = uuid4()
    _seed(documents, file_id, DocumentStatus.PROCESSING)
    app.dependency_overrides[get_document_repository] = lambda: documents

    with TestClient(app) as client:
        response = client.get(
            f"/api/internal/documents/{file_id}/status", headers=HEADERS
        )

    assert response.status_code == 200
    body = response.json()
    assert body["fileId"] == str(file_id)
    assert body["status"] == DocumentStatus.PROCESSING.value
    # The read model exposes only fileId + status — never s3 keys or error detail.
    assert set(body.keys()) == {"fileId", "status"}


def test_status_unknown_document_returns_404() -> None:
    app = create_app()
    documents = InMemoryDocumentRepository()
    app.dependency_overrides[get_document_repository] = lambda: documents

    with TestClient(app) as client:
        response = client.get(
            f"/api/internal/documents/{uuid4()}/status", headers=HEADERS
        )

    assert response.status_code == 404


def test_status_requires_internal_secret() -> None:
    app = create_app()
    documents = InMemoryDocumentRepository()
    file_id = uuid4()
    _seed(documents, file_id, DocumentStatus.DONE)
    app.dependency_overrides[get_document_repository] = lambda: documents

    with TestClient(app) as client:
        response = client.get(f"/api/internal/documents/{file_id}/status")

    assert response.status_code == 401
