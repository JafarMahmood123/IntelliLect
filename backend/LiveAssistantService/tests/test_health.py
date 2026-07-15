from __future__ import annotations

import pytest
from fastapi.testclient import TestClient

import app.api.routers.health as health_module
from app.api.main import create_app
from app.infrastructure.config.settings import get_settings

_LIVEKIT_ENV = ("LIVEKIT_URL", "LIVEKIT_API_KEY", "LIVEKIT_API_SECRET")


@pytest.fixture(autouse=True)
def _clear_settings_cache():
    get_settings.cache_clear()
    yield
    get_settings.cache_clear()


def _async_return(value):
    async def _check(*_args, **_kwargs):
        return value

    return _check


def _client(monkeypatch, *, knowledge="not-configured", ollama="not-configured"):
    # Component probes are mocked so /health makes no network calls.
    monkeypatch.setattr(health_module, "_check_knowledge_service", _async_return(knowledge))
    monkeypatch.setattr(health_module, "_check_ollama", _async_return(ollama))
    return TestClient(create_app())


def test_health_ok_when_no_external_component_is_unreachable(monkeypatch):
    for key in _LIVEKIT_ENV:
        monkeypatch.delenv(key, raising=False)
    get_settings.cache_clear()

    response = _client(monkeypatch).get("/health")

    assert response.status_code == 200
    body = response.json()
    assert body["status"] == "ok"
    assert body["livekit"] == "not-configured"
    assert body["knowledgeService"] == "not-configured"
    assert body["ollama"] == "not-configured"
    assert body["stt"] == {"model": get_settings().stt_model, "status": "configured"}
    assert body["activeSessions"] == 0
    assert body["metrics"] == "enabled"


def test_health_reports_livekit_configured(monkeypatch):
    monkeypatch.setenv("LIVEKIT_URL", "ws://livekit:7880")
    monkeypatch.setenv("LIVEKIT_API_KEY", "devkey")
    monkeypatch.setenv("LIVEKIT_API_SECRET", "devsecret")
    get_settings.cache_clear()

    body = _client(monkeypatch).get("/health").json()

    assert body["livekit"] == "configured"


def test_health_degraded_when_a_configured_component_is_unreachable(monkeypatch):
    body = _client(monkeypatch, knowledge="reachable", ollama="unreachable").get("/health").json()

    assert body["status"] == "degraded"
    assert body["ollama"] == "unreachable"
    assert body["knowledgeService"] == "reachable"
