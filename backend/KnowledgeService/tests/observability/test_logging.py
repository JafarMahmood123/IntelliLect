from __future__ import annotations

import logging
from uuid import uuid4

from app.application.services.ingestion_errors import PermanentIngestionError
from app.application.services.ingestion_service import IngestionJob
from app.domain.entities.document import Document
from app.domain.enums.document_status import DocumentStatus
from app.infrastructure.config.settings import Settings
from app.observability.logging_config import CorrelationFilter, JsonFormatter

from tests.extraction.fixtures import DOCX_CONTENT_TYPE, make_docx
from tests.ingestion.fakes import (
    FakeClock,
    FakeEmbeddingProvider,
    FakeFileStorage,
    InMemoryChunkRepository,
    InMemoryDocumentRepository,
    RaisingEmbeddingProvider,
    build_ingestion_service,
)

DIM = Settings().embedding_dim
# A phrase from the docx body — it must NEVER appear in any log line.
DOC_TEXT_PHRASE = "goal is to extract"


class _CaptureHandler(logging.Handler):
    def __init__(self) -> None:
        super().__init__(level=logging.DEBUG)
        self.addFilter(CorrelationFilter())  # populate file_id/run_id like production
        self.records: list[logging.LogRecord] = []

    def emit(self, record: logging.LogRecord) -> None:
        self.records.append(record)


def _job() -> IngestionJob:
    return IngestionJob(uuid4(), uuid4(), "class/doc.docx", "doc.docx", DOCX_CONTENT_TYPE)


def _seed(documents: InMemoryDocumentRepository, job: IngestionJob) -> None:
    documents.seed(
        Document(
            classroom_id=job.classroom_id, file_id=job.file_id, s3_key=job.s3_key,
            file_name=job.file_name, content_type=job.content_type,
            status=DocumentStatus.PENDING,
        )
    )


async def _run_capturing(service, job) -> list[logging.LogRecord]:
    handler = _CaptureHandler()
    root = logging.getLogger()
    previous_level = root.level
    root.addHandler(handler)
    root.setLevel(logging.DEBUG)
    try:
        await service.ingest(job)
    finally:
        root.removeHandler(handler)
        root.setLevel(previous_level)
    return handler.records


async def test_lifecycle_events_logged_at_info_with_correlation_and_no_text() -> None:
    job = _job()
    documents = InMemoryDocumentRepository()
    _seed(documents, job)
    service = build_ingestion_service(
        storage=FakeFileStorage({job.s3_key: make_docx()}),
        embedder=FakeEmbeddingProvider(DIM),
        documents=documents, chunks=InMemoryChunkRepository(), clock=FakeClock(),
    )

    records = await _run_capturing(service, job)
    messages = [r.getMessage() for r in records]

    # The key lifecycle events are emitted at INFO.
    for event in ("claimed", "extracted", "chunked", "embedded", "done"):
        assert event in messages, f"missing lifecycle event: {event}"
    by_msg = {r.getMessage(): r for r in records}
    for event in ("claimed", "extracted", "chunked", "embedded", "done"):
        assert by_msg[event].levelno == logging.INFO

    # Every lifecycle record carries the document's correlation id.
    lifecycle = [r for r in records if r.getMessage() in {"claimed", "extracted", "done"}]
    assert lifecycle and all(r.file_id == str(job.file_id) for r in lifecycle)

    # Structured counts are present as extras; the "extracted" event carries block/image
    # counts (ints), never any text.
    extracted = by_msg["extracted"]
    assert isinstance(getattr(extracted, "blocks"), int)

    # No document text (or auth secret) ever appears in the rendered logs.
    formatter = JsonFormatter()
    rendered = "\n".join(formatter.format(r) for r in records)
    assert DOC_TEXT_PHRASE not in rendered
    assert "test-internal-secret" not in rendered


async def test_failure_logs_error_type_not_the_raw_message() -> None:
    job = _job()
    documents = InMemoryDocumentRepository()
    _seed(documents, job)
    sensitive = "SENSITIVE-DETAIL-XYZ"
    service = build_ingestion_service(
        storage=FakeFileStorage({job.s3_key: make_docx()}),
        embedder=RaisingEmbeddingProvider(DIM, PermanentIngestionError(sensitive)),
        documents=documents, chunks=InMemoryChunkRepository(), clock=FakeClock(),
    )

    records = await _run_capturing(service, job)
    by_msg = {r.getMessage(): r for r in records}

    assert "failed" in by_msg
    failed = by_msg["failed"]
    assert failed.levelno == logging.ERROR
    assert getattr(failed, "error_type") == "PermanentIngestionError"

    # The raw exception message (which could carry sensitive detail) is never logged.
    rendered = "\n".join(JsonFormatter().format(r) for r in records)
    assert sensitive not in rendered
