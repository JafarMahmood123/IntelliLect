from __future__ import annotations

import app.api.routers.health as health_module
from app.api.main import create_app
from fastapi.testclient import TestClient


class _FakeWorker:
    def __init__(self, running: bool = True, depth: int = 0) -> None:
        self._running = running
        self._depth = depth

    def is_running(self) -> bool:
        return self._running

    def queue_depth(self) -> int:
        return self._depth


def _async_return(value):
    async def _check(*_args, **_kwargs):
        return value

    return _check


def _client(monkeypatch, *, db_ok: bool, ollama_ok: bool, worker: _FakeWorker | None):
    # Patch the probes so the test needs neither PostgreSQL nor Ollama.
    monkeypatch.setattr(health_module, "_check_db", _async_return(db_ok))
    monkeypatch.setattr(health_module, "_check_ollama", _async_return(ollama_ok))
    app = create_app()
    if worker is not None:
        app.state.ingestion_worker = worker
    return TestClient(app)


def test_health_ok_when_all_components_healthy(monkeypatch):
    client = _client(monkeypatch, db_ok=True, ollama_ok=True, worker=_FakeWorker(True, 3))
    response = client.get("/health")

    assert response.status_code == 200
    assert response.json() == {
        "status": "ok",
        "db": "ok",
        "ollama": "reachable",
        "worker": "running",
        "queueDepth": 3,
    }


def test_health_503_and_degraded_when_db_unreachable(monkeypatch):
    client = _client(monkeypatch, db_ok=False, ollama_ok=True, worker=_FakeWorker(True, 0))
    response = client.get("/health")

    assert response.status_code == 503
    body = response.json()
    assert body["status"] == "degraded"
    assert body["db"] == "fail"


def test_health_degraded_when_ollama_unreachable(monkeypatch):
    client = _client(monkeypatch, db_ok=True, ollama_ok=False, worker=_FakeWorker(True, 0))
    response = client.get("/health")

    assert response.status_code == 200  # db is fine -> still alive
    body = response.json()
    assert body["status"] == "degraded"
    assert body["ollama"] == "unreachable"


def test_health_degraded_when_worker_down(monkeypatch):
    client = _client(monkeypatch, db_ok=True, ollama_ok=True, worker=_FakeWorker(False, 0))
    response = client.get("/health")

    assert response.status_code == 200
    body = response.json()
    assert body["status"] == "degraded"
    assert body["worker"] == "down"
