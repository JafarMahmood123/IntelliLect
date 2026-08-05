"""Prometheus metrics for the live assistant pipeline (LA-8).

Additive instrumentation — recording is a no-op when metrics are disabled, and never
changes pipeline results. All helpers guard on ``_enabled`` so callers can stay
unconditional.
"""

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

SESSIONS_STARTED = Counter(
    "sessions_started_total", "Agent session pipelines started."
)
IDEAS_DETECTED = Counter(
    "ideas_detected_total",
    "Completed ideas emitted by boundary detection, by trigger.",
    ["trigger"],  # Drift | Pause | TimeCap | TokenCap
)
EVALUATIONS = Counter(
    "evaluations_total",
    "Ideas evaluated against course material, by whether feedback resulted.",
    ["has_feedback"],  # "true" | "false"
)
SUGGESTIONS_DELIVERED = Counter(
    "suggestions_delivered_total",
    "Suggestions delivered to the teacher, by feedback type.",
    ["type"],  # Discrepancy | Gap | Likely
)
SUGGESTIONS_SUPPRESSED = Counter(
    "suggestions_suppressed_total",
    "Suggestions suppressed by the pacing gate, by reason.",
    ["reason"],  # RateLimited | LowConfidence | Duplicate | SessionCap
)

STT_SEGMENT_LATENCY = Histogram(
    "stt_segment_latency_seconds", "Time to transcribe one audio window into a segment."
)
IDEA_TO_FEEDBACK_LATENCY = Histogram(
    "idea_to_feedback_latency_seconds",
    "Idea completion -> feedback delivered (the key NFR signal; target 3-5s).",
)
STAGE_LATENCY = Histogram(
    "stage_latency_seconds",
    "Per-stage pipeline latency.",
    ["stage"],  # stt | boundary | retrieval | evaluation | delivery
)

ACTIVE_SESSIONS = Gauge("active_sessions", "Agent session pipelines currently running.")

_enabled = True


def set_enabled(value: bool) -> None:
    global _enabled
    _enabled = value


def is_enabled() -> bool:
    return _enabled


# --- Recording helpers (no-ops when disabled) ----------------------------------


def record_session_started() -> None:
    if _enabled:
        SESSIONS_STARTED.inc()


def active_sessions_inc() -> None:
    if _enabled:
        ACTIVE_SESSIONS.inc()


def active_sessions_dec() -> None:
    if _enabled:
        ACTIVE_SESSIONS.dec()


def record_idea(trigger: str) -> None:
    if _enabled:
        IDEAS_DETECTED.labels(trigger=trigger).inc()


def record_evaluation(has_feedback: bool) -> None:
    if _enabled:
        EVALUATIONS.labels(has_feedback="true" if has_feedback else "false").inc()


def record_delivered(feedback_type: str) -> None:
    if _enabled:
        SUGGESTIONS_DELIVERED.labels(type=feedback_type).inc()


def record_suppressed(reason: str) -> None:
    if _enabled:
        SUGGESTIONS_SUPPRESSED.labels(reason=reason).inc()


def observe_idea_to_feedback(seconds: float) -> None:
    if _enabled:
        IDEA_TO_FEEDBACK_LATENCY.observe(seconds)


def observe_stage(stage: str, seconds: float) -> None:
    if _enabled:
        STAGE_LATENCY.labels(stage=stage).observe(seconds)


@contextmanager
def stage_timer(stage: str) -> Iterator[None]:
    start = time.perf_counter()
    try:
        yield
    finally:
        observe_stage(stage, time.perf_counter() - start)


@contextmanager
def stt_segment_timer() -> Iterator[None]:
    """Time one STT window; records both the segment-latency and stage=stt histograms."""
    start = time.perf_counter()
    try:
        yield
    finally:
        elapsed = time.perf_counter() - start
        if _enabled:
            STT_SEGMENT_LATENCY.observe(elapsed)
            STAGE_LATENCY.labels(stage="stt").observe(elapsed)


def render() -> tuple[bytes, str]:
    """Prometheus text exposition of all metrics (for GET /metrics)."""
    return generate_latest(), CONTENT_TYPE_LATEST
