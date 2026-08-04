"""Super-admin knowledge-base management endpoints: list, status-batch, detail, stats, bulk reindex."""

from __future__ import annotations

from uuid import uuid4

from fastapi.testclient import TestClient

from app.api.dependencies import (
    get_document_repository,
    get_ingestion_worker,
)
from app.api.main import create_app
from app.domain.entities.document import Document
from app.domain.enums.document_status import DocumentStatus

from tests.ingestion.fakes import InMemoryDocumentRepository

HEADERS = {"X-Internal-Secret": "test-internal-secret"}


def _doc(documents, *, classroom_id, status=DocumentStatus.DONE, file_name="doc.pdf",
         size=1000, chunks=0, attempts=0) -> Document:
    file_id = uuid4()
    d = Document(
        classroom_id=classroom_id,
        file_id=file_id,
        s3_key="classrooms/x/doc.pdf",
        file_name=file_name,
        content_type="application/pdf",
        size_bytes=size,
        status=status,
        attempts=attempts,
    )
    documents.seed(d)
    documents.chunk_counts[d.id] = chunks
    return d


class _FullQueueWorker:
    """Worker whose queue is always full (enqueue returns False) — exercises 7د."""

    def enqueue(self, job) -> bool:  # noqa: ARG002
        return False


class _RecordingWorker:
    def __init__(self) -> None:
        self.count = 0

    def enqueue(self, job) -> bool:  # noqa: ARG002
        self.count += 1
        return True


def _client(documents, worker=None) -> TestClient:
    app = create_app()
    app.dependency_overrides[get_document_repository] = lambda: documents
    if worker is not None:
        app.dependency_overrides[get_ingestion_worker] = lambda: worker
    return TestClient(app)


def test_list_documents_paged_and_filtered_by_status():
    documents = InMemoryDocumentRepository()
    cid = uuid4()
    _doc(documents, classroom_id=cid, status=DocumentStatus.DONE, file_name="alpha.pdf", chunks=3)
    _doc(documents, classroom_id=cid, status=DocumentStatus.FAILED, file_name="beta.pdf")

    with _client(documents) as client:
        # Unfiltered
        r = client.get("/api/internal/documents", params={"page": 1, "pageSize": 10}, headers=HEADERS)
        assert r.status_code == 200
        body = r.json()
        assert body["total"] == 2
        assert {i["fileName"] for i in body["items"]} == {"alpha.pdf", "beta.pdf"}

        # Filter by status
        r = client.get("/api/internal/documents", params={"status": "Failed"}, headers=HEADERS)
        assert r.status_code == 200
        items = r.json()["items"]
        assert len(items) == 1
        assert items[0]["fileName"] == "beta.pdf"
        assert items[0]["status"] == "Failed"


def test_list_documents_search_by_filename():
    documents = InMemoryDocumentRepository()
    cid = uuid4()
    _doc(documents, classroom_id=cid, file_name="lecture-notes.pdf")
    _doc(documents, classroom_id=cid, file_name="syllabus.pdf")

    with _client(documents) as client:
        r = client.get("/api/internal/documents", params={"search": "lecture"}, headers=HEADERS)
    assert r.json()["total"] == 1


def test_status_batch_returns_only_known_ids_with_chunk_counts():
    documents = InMemoryDocumentRepository()
    cid = uuid4()
    a = _doc(documents, classroom_id=cid, chunks=5)
    unknown = uuid4()

    with _client(documents) as client:
        r = client.post(
            "/api/internal/documents/status-batch",
            json={"fileIds": [str(a.file_id), str(unknown)]},
            headers=HEADERS,
        )
    assert r.status_code == 200
    items = r.json()
    assert len(items) == 1
    assert items[0]["fileId"] == str(a.file_id)
    assert items[0]["chunkCount"] == 5


def test_document_detail_exposes_failure_reason():
    documents = InMemoryDocumentRepository()
    d = _doc(documents, classroom_id=uuid4(), status=DocumentStatus.FAILED, attempts=3)
    # Attach an error via the repo's lifecycle.
    import anyio
    anyio.run(documents.update_status, d.file_id, DocumentStatus.FAILED, "OCR timed out")

    with _client(documents) as client:
        r = client.get(f"/api/internal/documents/{d.file_id}/detail", headers=HEADERS)
    assert r.status_code == 200
    body = r.json()
    assert body["status"] == "Failed"
    assert body["attempts"] == 3
    assert body["lastError"] == "OCR timed out"


def test_document_detail_unknown_is_404():
    documents = InMemoryDocumentRepository()
    with _client(documents) as client:
        r = client.get(f"/api/internal/documents/{uuid4()}/detail", headers=HEADERS)
    assert r.status_code == 404  # 7أ


def test_stats_aggregates_by_status_and_storage():
    documents = InMemoryDocumentRepository()
    cid = uuid4()
    _doc(documents, classroom_id=cid, status=DocumentStatus.DONE, size=1000, chunks=4)
    _doc(documents, classroom_id=cid, status=DocumentStatus.DONE, size=500, chunks=2)
    _doc(documents, classroom_id=cid, status=DocumentStatus.FAILED, size=200)

    with _client(documents) as client:
        r = client.get("/api/internal/knowledge/stats", params={"classroomId": str(cid)}, headers=HEADERS)
    assert r.status_code == 200
    body = r.json()
    assert body["documentCount"] == 3
    assert body["statusCounts"]["Done"] == 2
    assert body["statusCounts"]["Failed"] == 1
    assert body["failedCount"] == 1
    assert body["totalChunks"] == 6
    assert body["storageBytes"] == 1700


def test_bulk_reindex_enqueues_and_reports_counts():
    documents = InMemoryDocumentRepository()
    cid = uuid4()
    _doc(documents, classroom_id=cid, status=DocumentStatus.FAILED)
    _doc(documents, classroom_id=cid, status=DocumentStatus.DONE)
    worker = _RecordingWorker()

    with _client(documents, worker) as client:
        r = client.post(
            f"/api/internal/documents/classrooms/{cid}/reindex",
            params={"failedOnly": "true"},
            headers=HEADERS,
        )
    assert r.status_code == 202
    body = r.json()
    assert body["requested"] == 1  # only the failed one
    assert body["enqueued"] == 1
    assert body["skipped"] == 0
    assert worker.count == 1


def test_bulk_reindex_rejects_when_active_indexing(monkeypatch):
    documents = InMemoryDocumentRepository()
    cid = uuid4()
    _doc(documents, classroom_id=cid, status=DocumentStatus.PROCESSING)  # in flight
    worker = _RecordingWorker()

    with _client(documents, worker) as client:
        r = client.post(f"/api/internal/documents/classrooms/{cid}/reindex", headers=HEADERS)
    assert r.status_code == 409  # 7ج


def test_bulk_reindex_rejects_over_cap():
    documents = InMemoryDocumentRepository()
    cid = uuid4()
    # Seed more than the default bulk cap (50) so a whole-classroom reindex is rejected (7ب).
    for _ in range(51):
        _doc(documents, classroom_id=cid, status=DocumentStatus.DONE)

    with _client(documents, _RecordingWorker()) as client:
        r = client.post(f"/api/internal/documents/classrooms/{cid}/reindex", headers=HEADERS)
    assert r.status_code == 400  # 7ب


def test_bulk_reindex_counts_skipped_when_queue_full():
    documents = InMemoryDocumentRepository()
    cid = uuid4()
    _doc(documents, classroom_id=cid, status=DocumentStatus.FAILED)
    _doc(documents, classroom_id=cid, status=DocumentStatus.FAILED)

    with _client(documents, _FullQueueWorker()) as client:
        r = client.post(
            f"/api/internal/documents/classrooms/{cid}/reindex",
            params={"failedOnly": "true"},
            headers=HEADERS,
        )
    assert r.status_code == 202
    body = r.json()
    assert body["requested"] == 2
    assert body["enqueued"] == 0
    assert body["skipped"] == 2  # 7د: not lost, reported as skipped
