"""Internal session endpoints (LA-6) — start/stop/list, auth, health active count.

Uses a SessionManager with a FakePipelineFactory (no LiveKit, no models), set on
app.state directly so the app's lifespan isn't required.
"""

from __future__ import annotations

from uuid import uuid4

import pytest
from fastapi.testclient import TestClient

from app.api.main import create_app
from app.application.services.session_manager import SessionManager
from app.infrastructure.config.settings import get_settings
from tests.support.fake_pipeline import FakePipelineFactory

_SECRET = "test-internal-secret"
_HEADERS = {"X-Internal-Secret": _SECRET}


@pytest.fixture(autouse=True)
def _internal_secret_env(monkeypatch):
    monkeypatch.setenv("INTERNAL_API_SECRET", _SECRET)
    get_settings.cache_clear()
    yield
    get_settings.cache_clear()


def _client(max_concurrent: int = 10) -> tuple[TestClient, SessionManager]:
    app = create_app()
    manager = SessionManager(FakePipelineFactory(), max_concurrent)
    app.state.session_manager = manager  # bypass lifespan; inject the fake-backed manager
    return TestClient(app), manager


def _start_body(session_id=None) -> dict:
    return {
        "sessionId": str(session_id or uuid4()),
        "classroomId": str(uuid4()),
        "roomName": "room-123",
        "teacherIdentity": "teacher-1",
    }


def test_start_returns_202_and_registers():
    client, manager = _client()
    body = _start_body()

    response = client.post("/api/internal/sessions/start", json=body, headers=_HEADERS)

    assert response.status_code == 202
    assert response.json()["sessionId"] == body["sessionId"]
    assert manager.active_count() == 1


def test_start_is_idempotent_on_session_id():
    client, manager = _client()
    body = _start_body()

    client.post("/api/internal/sessions/start", json=body, headers=_HEADERS)
    second = client.post("/api/internal/sessions/start", json=body, headers=_HEADERS)

    assert second.status_code == 202
    assert manager.active_count() == 1  # still one


def test_stop_returns_204_and_deregisters():
    client, manager = _client()
    body = _start_body()
    client.post("/api/internal/sessions/start", json=body, headers=_HEADERS)

    response = client.post(f"/api/internal/sessions/{body['sessionId']}/stop", headers=_HEADERS)

    assert response.status_code == 204
    assert manager.active_count() == 0


def test_stop_unknown_session_is_204():
    client, _ = _client()
    response = client.post(f"/api/internal/sessions/{uuid4()}/stop", headers=_HEADERS)
    assert response.status_code == 204


def test_list_active_sessions():
    client, _ = _client()
    body = _start_body()
    client.post("/api/internal/sessions/start", json=body, headers=_HEADERS)

    response = client.get("/api/internal/sessions", headers=_HEADERS)

    assert response.status_code == 200
    payload = response.json()
    assert payload["count"] == 1
    assert body["sessionId"] in payload["activeSessions"]


def test_capacity_cap_returns_503():
    client, _ = _client(max_concurrent=1)
    client.post("/api/internal/sessions/start", json=_start_body(), headers=_HEADERS)

    response = client.post("/api/internal/sessions/start", json=_start_body(), headers=_HEADERS)

    assert response.status_code == 503


def test_missing_secret_is_unauthorized():
    client, _ = _client()
    response = client.post("/api/internal/sessions/start", json=_start_body())
    assert response.status_code == 401


def test_wrong_secret_is_unauthorized():
    client, _ = _client()
    response = client.post(
        "/api/internal/sessions/start", json=_start_body(), headers={"X-Internal-Secret": "nope"}
    )
    assert response.status_code == 401


def test_health_reports_active_session_count():
    client, _ = _client()
    client.post("/api/internal/sessions/start", json=_start_body(), headers=_HEADERS)

    response = client.get("/health")

    assert response.status_code == 200
    assert response.json()["activeSessions"] == 1
