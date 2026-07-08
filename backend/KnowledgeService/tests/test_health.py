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


def test_health_ok_when_db_reachable(monkeypatch):
    # Patch the DB access so the test does not need a live PostgreSQL.
    monkeypatch.setattr(health_module, "get_session_factory", lambda: _fake_session_factory)

    client = TestClient(create_app())
    response = client.get("/health")

    assert response.status_code == 200
    assert response.json() == {"status": "ok", "db": "ok"}


def test_health_reports_fail_when_db_unreachable(monkeypatch):
    def _boom():
        raise RuntimeError("db down")

    monkeypatch.setattr(health_module, "get_session_factory", _boom)

    client = TestClient(create_app())
    response = client.get("/health")

    assert response.status_code == 503
    assert response.json() == {"status": "degraded", "db": "fail"}
