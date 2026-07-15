"""GET /metrics (LA-8): Prometheus exposition when enabled; absent when disabled."""

from __future__ import annotations

import pytest
from fastapi.testclient import TestClient

from app.api.main import create_app
from app.infrastructure.config.settings import get_settings
from app.observability import metrics


@pytest.fixture(autouse=True)
def _restore_metrics():
    get_settings.cache_clear()
    yield
    metrics.set_enabled(True)  # other tests expect metrics enabled
    get_settings.cache_clear()


def test_metrics_endpoint_exposes_prometheus_text(monkeypatch):
    monkeypatch.setenv("METRICS_ENABLED", "true")
    get_settings.cache_clear()

    response = TestClient(create_app()).get("/metrics")

    assert response.status_code == 200
    assert "text/plain" in response.headers["content-type"]
    # A known metric name is present in the exposition.
    assert "sessions_started_total" in response.text


def test_metrics_endpoint_absent_when_disabled(monkeypatch):
    monkeypatch.setenv("METRICS_ENABLED", "false")
    get_settings.cache_clear()

    response = TestClient(create_app()).get("/metrics")

    assert response.status_code == 404
    assert metrics.is_enabled() is False
