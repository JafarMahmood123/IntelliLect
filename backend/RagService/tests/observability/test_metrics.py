from __future__ import annotations

from uuid import uuid4

from prometheus_client import REGISTRY

from app.application.services.ingestion_service import IngestionJob
from app.domain.entities.document import Document
from app.domain.enums.document_status import DocumentStatus
from app.infrastructure.config.settings import Settings
from app.observability import metrics

from tests.extraction.fixtures import DOCX_CONTENT_TYPE, make_docx
from tests.ingestion.fakes import (
    FakeClock,
    FakeEmbeddingProvider,
    FakeFileStorage,
    InMemoryChunkRepository,
    InMemoryDocumentRepository,
    build_ingestion_service,
)

DIM = Settings().embedding_dim


def _value(name: str, labels: dict | None = None) -> float:
    return REGISTRY.get_sample_value(name, labels or {}) or 0.0


def _job(s3_key: str) -> IngestionJob:
    return IngestionJob(uuid4(), uuid4(), s3_key, "doc.docx", DOCX_CONTENT_TYPE)


def _seed(documents: InMemoryDocumentRepository, job: IngestionJob) -> None:
    documents.seed(
        Document(
            classroom_id=job.classroom_id, file_id=job.file_id, s3_key=job.s3_key,
            file_name=job.file_name, content_type=job.content_type,
            status=DocumentStatus.PENDING,
        )
    )


async def test_counters_and_histograms_move_on_successful_ingest() -> None:
    metrics.set_enabled(True)
    job = _job("class/metrics.docx")
    documents = InMemoryDocumentRepository()
    _seed(documents, job)
    service = build_ingestion_service(
        storage=FakeFileStorage({job.s3_key: make_docx()}),
        embedder=FakeEmbeddingProvider(DIM),
        documents=documents, chunks=InMemoryChunkRepository(), clock=FakeClock(),
    )

    before = {
        "done": _value("documents_ingested_total", {"status": "done"}),
        "embeddings": _value("embeddings_requested_total"),
        "extract_count": _value("stage_duration_seconds_count", {"stage": "extract"}),
        "persist_count": _value("stage_duration_seconds_count", {"stage": "persist"}),
        "ingestion_count": _value("ingestion_duration_seconds_count"),
        "chunks_count": _value("chunks_per_document_count"),
    }

    result = await service.ingest(job)
    assert result.status == DocumentStatus.DONE

    assert _value("documents_ingested_total", {"status": "done"}) == before["done"] + 1
    assert _value("embeddings_requested_total") > before["embeddings"]
    assert _value("stage_duration_seconds_count", {"stage": "extract"}) == before["extract_count"] + 1
    assert _value("stage_duration_seconds_count", {"stage": "persist"}) == before["persist_count"] + 1
    assert _value("ingestion_duration_seconds_count") == before["ingestion_count"] + 1
    assert _value("chunks_per_document_count") == before["chunks_count"] + 1


async def test_failure_counters_move() -> None:
    metrics.set_enabled(True)
    job = _job("class/bad.pdf")
    # Corrupt PDF -> ExtractionError -> permanent failure.
    documents = InMemoryDocumentRepository()
    documents.seed(
        Document(
            classroom_id=job.classroom_id, file_id=job.file_id, s3_key=job.s3_key,
            file_name="bad.pdf", content_type="application/pdf",
            status=DocumentStatus.PENDING,
        )
    )
    service = build_ingestion_service(
        storage=FakeFileStorage({job.s3_key: b"%PDF-1.4 not a real pdf"}),
        embedder=FakeEmbeddingProvider(DIM),
        documents=documents, chunks=InMemoryChunkRepository(), clock=FakeClock(),
    )

    before_failed = _value("documents_ingested_total", {"status": "failed"})
    before_permanent = _value("ingestion_failures_total", {"type": "permanent"})

    result = await service.ingest(job)
    assert result.status == DocumentStatus.FAILED

    assert _value("documents_ingested_total", {"status": "failed"}) == before_failed + 1
    assert _value("ingestion_failures_total", {"type": "permanent"}) == before_permanent + 1


def test_metrics_render_is_prometheus_text() -> None:
    payload, content_type = metrics.render()
    assert b"documents_ingested_total" in payload
    assert "text/plain" in content_type


def test_metrics_endpoint_is_exposed() -> None:
    from fastapi.testclient import TestClient

    from app.api.main import create_app

    with TestClient(create_app()) as client:
        response = client.get("/metrics")

    assert response.status_code == 200
    assert "stage_duration_seconds" in response.text
