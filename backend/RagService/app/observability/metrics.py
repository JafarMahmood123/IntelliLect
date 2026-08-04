from __future__ import annotations

import time
from collections.abc import Iterator
from contextlib import contextmanager

from prometheus_client import (
    CONTENT_TYPE_LATEST,
    Counter,
    Gauge,
    Histogram,
    generate_latest,
)

# --- Metric definitions (registered once in the default registry) --------------

DOCUMENTS_INGESTED = Counter(
    "documents_ingested_total",
    "Documents processed, by terminal status (done/failed/skipped).",
    ["status"],
)
OCR_INVOCATIONS = Counter(
    "ocr_invocations_total", "Total Tesseract OCR invocations."
)
EMBEDDINGS_REQUESTED = Counter(
    "embeddings_requested_total", "Total chunk texts sent to the embedder."
)
INGESTION_FAILURES = Counter(
    "ingestion_failures_total", "Ingestion failures, by type.", ["type"]
)
INGESTION_DURATION = Histogram(
    "ingestion_duration_seconds", "End-to-end ingestion duration per document."
)
STAGE_DURATION = Histogram(
    "stage_duration_seconds",
    "Per-stage ingestion duration.",
    ["stage"],  # extract | ocr | chunk | embed | persist
)
CHUNKS_PER_DOCUMENT = Histogram(
    "chunks_per_document",
    "Chunks produced per document.",
    buckets=(0, 1, 5, 10, 25, 50, 100, 250, 500, 1000),
)
INGEST_QUEUE_DEPTH = Gauge("ingest_queue_depth", "Current ingestion queue depth.")
INFLIGHT_DOCUMENTS = Gauge(
    "inflight_documents", "Documents currently being processed."
)

# --- Session summary (S-5) ------------------------------------------------------
SUMMARIES_GENERATED = Counter(
    "summaries_generated_total", "Session summaries generated successfully."
)
SUMMARIES_FAILED = Counter(
    "summaries_failed_total", "Session summaries that failed to generate."
)
SUMMARIES_GROUNDED = Counter(
    "summaries_grounded_total",
    "Summaries generated with classroom-material grounding.",
)
SUMMARY_GENERATION_DURATION = Histogram(
    "summary_generation_seconds", "Time to generate the summary Markdown."
)
SUMMARY_RENDER_DURATION = Histogram(
    "summary_render_seconds", "Time to render the summary PDF."
)
SUMMARY_TRANSCRIPT_TOKENS = Histogram(
    "summary_transcript_tokens",
    "Transcript tokens fed to summarization.",
    buckets=(0, 100, 250, 500, 1000, 2000, 4000, 8000, 16000, 32000),
)

_enabled = True


def set_enabled(value: bool) -> None:
    global _enabled
    _enabled = value


def is_enabled() -> bool:
    return _enabled


# --- Recording helpers (no-ops when metrics are disabled) ----------------------


@contextmanager
def stage_timer(stage: str) -> Iterator[None]:
    start = time.perf_counter()
    try:
        yield
    finally:
        if _enabled:
            STAGE_DURATION.labels(stage=stage).observe(time.perf_counter() - start)


@contextmanager
def ingestion_timer() -> Iterator[None]:
    start = time.perf_counter()
    try:
        yield
    finally:
        if _enabled:
            INGESTION_DURATION.observe(time.perf_counter() - start)


@contextmanager
def track_inflight() -> Iterator[None]:
    if _enabled:
        INFLIGHT_DOCUMENTS.inc()
    try:
        yield
    finally:
        if _enabled:
            INFLIGHT_DOCUMENTS.dec()


def record_document(status: str) -> None:
    if _enabled:
        DOCUMENTS_INGESTED.labels(status=status).inc()


def record_failure(failure_type: str) -> None:
    if _enabled:
        INGESTION_FAILURES.labels(type=failure_type).inc()


def record_ocr_invocations(count: int) -> None:
    if _enabled and count:
        OCR_INVOCATIONS.inc(count)


def record_embeddings(count: int) -> None:
    if _enabled and count:
        EMBEDDINGS_REQUESTED.inc(count)


def record_chunks(count: int) -> None:
    if _enabled:
        CHUNKS_PER_DOCUMENT.observe(count)


def set_queue_depth(depth: int) -> None:
    if _enabled:
        INGEST_QUEUE_DEPTH.set(depth)


# --- Session summary recording helpers (S-5) -----------------------------------


def record_summary_generated() -> None:
    if _enabled:
        SUMMARIES_GENERATED.inc()


def record_summary_failed() -> None:
    if _enabled:
        SUMMARIES_FAILED.inc()


def record_summary_grounded() -> None:
    if _enabled:
        SUMMARIES_GROUNDED.inc()


def observe_summary_generation(seconds: float) -> None:
    if _enabled:
        SUMMARY_GENERATION_DURATION.observe(seconds)


def observe_summary_render(seconds: float) -> None:
    if _enabled:
        SUMMARY_RENDER_DURATION.observe(seconds)


def observe_summary_transcript_tokens(count: int) -> None:
    if _enabled:
        SUMMARY_TRANSCRIPT_TOKENS.observe(count)


def render() -> tuple[bytes, str]:
    """Prometheus text exposition of all metrics (for GET /metrics)."""
    return generate_latest(), CONTENT_TYPE_LATEST
