"""Session-end summarize trigger (S-3) — 202, enqueue, auth, flag, non-fatal. Offline."""

from __future__ import annotations

from uuid import uuid4

from fastapi.testclient import TestClient

from app.api.dependencies import get_summary_runner
from app.api.main import create_app
from app.application.services.summary_runner import SummaryRunner
from app.infrastructure.config.settings import get_settings

HEADERS = {"X-Internal-Secret": "test-internal-secret"}


class _SpyRunner:
    def __init__(self, started: bool = True) -> None:
        self.enqueued: list = []
        self._started = started

    def enqueue(self, session_id, classroom_id) -> bool:
        self.enqueued.append((session_id, classroom_id))
        return self._started


def _body(classroom_id=None) -> dict:
    return {"classroomId": str(classroom_id or uuid4())}


def test_summarize_returns_202_and_enqueues():
    app = create_app()
    runner = _SpyRunner()
    app.dependency_overrides[get_summary_runner] = lambda: runner
    session_id = uuid4()
    classroom_id = uuid4()

    with TestClient(app) as client:
        response = client.post(
            f"/api/internal/sessions/{session_id}/summarize",
            json=_body(classroom_id),
            headers=HEADERS,
        )

    assert response.status_code == 202
    assert response.json()["status"] == "accepted"
    assert runner.enqueued == [(session_id, classroom_id)]


def test_summarize_reports_in_progress_when_already_running():
    app = create_app()
    app.dependency_overrides[get_summary_runner] = lambda: _SpyRunner(started=False)

    with TestClient(app) as client:
        response = client.post(
            f"/api/internal/sessions/{uuid4()}/summarize", json=_body(), headers=HEADERS
        )

    assert response.status_code == 202
    assert response.json()["status"] == "in-progress"


def test_summarize_requires_internal_secret():
    app = create_app()
    app.dependency_overrides[get_summary_runner] = lambda: _SpyRunner()

    with TestClient(app) as client:
        response = client.post(
            f"/api/internal/sessions/{uuid4()}/summarize", json=_body()
        )

    assert response.status_code == 401


def test_summarize_skips_when_trigger_disabled(monkeypatch):
    monkeypatch.setenv("SUMMARY_TRIGGER_ENABLED", "false")
    get_settings.cache_clear()
    try:
        app = create_app()
        runner = _SpyRunner()
        app.dependency_overrides[get_summary_runner] = lambda: runner

        with TestClient(app) as client:
            response = client.post(
                f"/api/internal/sessions/{uuid4()}/summarize",
                json=_body(),
                headers=HEADERS,
            )

        assert response.status_code == 202
        assert response.json()["status"] == "skipped"
        assert runner.enqueued == []  # the pipeline is NOT started
    finally:
        get_settings.cache_clear()


def test_trigger_is_non_fatal_even_if_the_pipeline_fails():
    # Uses the REAL SummaryRunner with a handle that raises: the trigger still returns
    # 202 (the failure happens on the background task, not in the request).
    app = create_app()

    async def failing_handle(session_id, classroom_id):
        raise RuntimeError("pipeline exploded")

    real_runner = SummaryRunner(failing_handle)
    app.dependency_overrides[get_summary_runner] = lambda: real_runner

    with TestClient(app) as client:
        response = client.post(
            f"/api/internal/sessions/{uuid4()}/summarize", json=_body(), headers=HEADERS
        )

    assert response.status_code == 202  # caller unaffected by the background failure
