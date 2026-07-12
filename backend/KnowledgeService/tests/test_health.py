from contextlib import asynccontextmanager

import app.api.routers.health as health_module
from app.api.main import create_app
from fastapi.testclient import TestClient


class _FakeSession:
    async def execute(self, *_args, **_kwargs):
        return None


@asynccontextmanager
async def _fake_session_ctx():
    yield _FakeSession()


def _fake_session_factory():
    # Mimics `factory()` returning an async context manager.
    return _fake_session_ctx()


async def _ollama_reachable() -> bool:
    return True


async def _ollama_unreachable() -> bool:
    return False


def test_health_ok_when_db_reachable(monkeypatch):
    # Patch both probes so the test needs neither PostgreSQL nor Ollama.
    monkeypatch.setattr(health_module, "get_session_factory", lambda: _fake_session_factory)
    monkeypatch.setattr(health_module, "_check_ollama", _ollama_reachable)

    client = TestClient(create_app())
    response = client.get("/health")

    assert response.status_code == 200
    assert response.json() == {"status": "ok", "db": "ok", "ollama": "reachable"}


def test_health_reports_fail_when_db_unreachable(monkeypatch):
    def _boom():
        raise RuntimeError("db down")

    monkeypatch.setattr(health_module, "get_session_factory", _boom)
    # Ollama unreachable must NOT change the DB-driven status code.
    monkeypatch.setattr(health_module, "_check_ollama", _ollama_unreachable)

    client = TestClient(create_app())
    response = client.get("/health")

    assert response.status_code == 503
    assert response.json() == {"status": "degraded", "db": "fail", "ollama": "unreachable"}
