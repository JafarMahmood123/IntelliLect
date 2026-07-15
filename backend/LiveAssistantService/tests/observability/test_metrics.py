"""Metrics (LA-8): counters/histograms/gauges move on a fake-driven pipeline run."""

from __future__ import annotations

import pytest
from prometheus_client import REGISTRY

from app.observability import metrics
from tests.support.pipeline_harness import run_scenario


@pytest.fixture(autouse=True)
def _metrics_enabled():
    metrics.set_enabled(True)
    yield
    metrics.set_enabled(True)


def _value(name: str, labels: dict | None = None) -> float:
    return REGISTRY.get_sample_value(name, labels or {}) or 0.0


async def test_counters_histograms_and_gauge_move_on_a_run():
    before = {
        "sessions": _value("sessions_started_total"),
        "drift": _value("ideas_detected_total", {"trigger": "Drift"}),
        "pause": _value("ideas_detected_total", {"trigger": "Pause"}),
        "eval_true": _value("evaluations_total", {"has_feedback": "true"}),
        "eval_false": _value("evaluations_total", {"has_feedback": "false"}),
        "delivered": _value("suggestions_delivered_total", {"type": "Discrepancy"}),
        "suppressed": _value("suggestions_suppressed_total", {"reason": "RateLimited"}),
        "i2f_count": _value("idea_to_feedback_latency_seconds_count"),
        "boundary_count": _value("stage_latency_seconds_count", {"stage": "boundary"}),
        "retrieval_count": _value("stage_latency_seconds_count", {"stage": "retrieval"}),
        "evaluation_count": _value("stage_latency_seconds_count", {"stage": "evaluation"}),
        "delivery_count": _value("stage_latency_seconds_count", {"stage": "delivery"}),
        "active": _value("active_sessions"),
    }

    await run_scenario()

    # The scripted run: 4 ideas (3 Drift + 1 flushed Pause); 2 flagged / 2 not;
    # 1 delivered (Discrepancy); 1 rate-limited; 1 idea->feedback sample.
    assert _value("sessions_started_total") - before["sessions"] == 1
    assert _value("ideas_detected_total", {"trigger": "Drift"}) - before["drift"] == 3
    assert _value("ideas_detected_total", {"trigger": "Pause"}) - before["pause"] == 1
    assert _value("evaluations_total", {"has_feedback": "true"}) - before["eval_true"] == 2
    assert _value("evaluations_total", {"has_feedback": "false"}) - before["eval_false"] == 2
    assert _value("suggestions_delivered_total", {"type": "Discrepancy"}) - before["delivered"] == 1
    assert _value("suggestions_suppressed_total", {"reason": "RateLimited"}) - before["suppressed"] == 1
    assert _value("idea_to_feedback_latency_seconds_count") - before["i2f_count"] == 1

    # Stage timings recorded where the work happens.
    assert _value("stage_latency_seconds_count", {"stage": "boundary"}) - before["boundary_count"] == 4
    assert _value("stage_latency_seconds_count", {"stage": "retrieval"}) - before["retrieval_count"] == 4
    assert _value("stage_latency_seconds_count", {"stage": "evaluation"}) - before["evaluation_count"] == 4
    assert _value("stage_latency_seconds_count", {"stage": "delivery"}) - before["delivery_count"] == 1

    # The active-sessions gauge returns to its baseline after the run completes.
    assert _value("active_sessions") == before["active"]


async def test_recording_is_a_noop_when_metrics_disabled():
    metrics.set_enabled(False)
    before = _value("sessions_started_total")

    await run_scenario()

    assert _value("sessions_started_total") == before  # unchanged while disabled
