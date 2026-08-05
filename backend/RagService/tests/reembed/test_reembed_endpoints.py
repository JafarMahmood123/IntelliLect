"""The two operator-facing re-embed endpoints.

Both sit behind the internal secret and neither had a test. They are driven by curl during a
model migration — there is no UI — so the HTTP status is the contract: it is what `curl -f` and
every ad-hoc script actually branch on.
"""

from __future__ import annotations

import asyncio

from fastapi.testclient import TestClient

from app.api.dependencies import get_reembed_runner
from app.api.main import create_app
from app.application.services.reembed_runner import ReembedRunner
from app.application.services.reembed_service import ReembedProgress

HEADERS = {"X-Internal-Secret": "test-internal-secret"}


class _StubRunner:
    """Stands in for the runner so the endpoints are tested without a database or an embedder."""

    def __init__(self, *, running: bool = False, starts: bool = True, progress=None) -> None:
        self.running = running
        self.starts = starts
        self._progress = progress or ReembedProgress()
        self.start_calls = 0

    def start(self) -> bool:
        self.start_calls += 1
        return self.starts

    def is_running(self) -> bool:
        return self.running

    def progress(self) -> ReembedProgress:
        return self._progress


def _client(runner) -> TestClient:
    app = create_app()
    app.dependency_overrides[get_reembed_runner] = lambda: runner
    return TestClient(app)


def test_starting_a_sweep_is_accepted():
    runner = _StubRunner(progress=ReembedProgress(state="running", total=120, remaining=120))

    with _client(runner) as client:
        response = client.post("/api/internal/reembed", headers=HEADERS)

    assert response.status_code == 202
    assert response.json()["status"] == "accepted"
    assert response.json()["total"] == 120
    assert runner.start_calls == 1


def test_a_refused_second_sweep_answers_409_not_202():
    """The refusal has to be in the status line.

    A 202 for a run that was declined tells a script the opposite of what happened, and the
    natural reaction to "accepted but nothing seems to be happening" is to POST again.
    """
    runner = _StubRunner(running=True, starts=False)

    with _client(runner) as client:
        response = client.post("/api/internal/reembed", headers=HEADERS)

    assert response.status_code == 409
    assert response.json()["status"] == "already-running"


def test_the_status_endpoint_reports_the_run_and_its_progress():
    runner = _StubRunner(
        running=True,
        progress=ReembedProgress(state="running", total=500, embedded=180, remaining=320),
    )

    with _client(runner) as client:
        body = client.get("/api/internal/reembed/status", headers=HEADERS).json()

    assert body == {
        "running": True,
        "state": "running",
        "total": 500,
        "embedded": 180,
        "remaining": 320,
        "error": None,
    }


def test_a_failed_run_reports_its_reason_through_the_status_endpoint():
    # The only channel there is: the POST returned long before the failure happened.
    runner = _StubRunner(
        progress=ReembedProgress(state="failed", error="DimensionMismatchError: 1024 vs 3072")
    )

    with _client(runner) as client:
        body = client.get("/api/internal/reembed/status", headers=HEADERS).json()

    assert body["running"] is False
    assert body["state"] == "failed"
    assert "3072" in body["error"]


def test_both_routes_refuse_a_request_with_no_internal_secret():
    # This endpoint spends money — every chunk in the corpus is an embedding call — and the
    # status one describes the size of the knowledge base.
    runner = _StubRunner()

    with _client(runner) as client:
        assert client.post("/api/internal/reembed").status_code == 401
        assert client.get("/api/internal/reembed/status").status_code == 401
    assert runner.start_calls == 0


def test_both_routes_refuse_a_wrong_internal_secret():
    runner = _StubRunner()
    wrong = {"X-Internal-Secret": "not-the-secret"}

    with _client(runner) as client:
        assert client.post("/api/internal/reembed", headers=wrong).status_code == 401
        assert client.get("/api/internal/reembed/status", headers=wrong).status_code == 401
    assert runner.start_calls == 0


def test_the_endpoints_report_503_when_no_runner_was_built():
    """`get_reembed_runner` reads the runner off `app.state`, where startup puts it.

    If startup failed before that point the attribute is simply absent, and without this the
    request would raise an AttributeError and answer 500 — indistinguishable from a bug in the
    sweep itself.
    """
    app = create_app()

    with TestClient(app) as client:
        app.state.reembed_runner = None
        assert client.post("/api/internal/reembed", headers=HEADERS).status_code == 503
        assert client.get("/api/internal/reembed/status", headers=HEADERS).status_code == 503


def test_the_real_runner_is_wired_up_at_startup():
    # The stub above proves the endpoints behave; this proves they are pointed at something.
    app = create_app()

    with TestClient(app):
        assert isinstance(app.state.reembed_runner, ReembedRunner)
        assert app.state.reembed_runner.is_running() is False


def test_a_real_runner_refuses_a_concurrent_start_through_the_endpoint():
    """End to end through HTTP, with the real single-flight logic rather than a stub.

    The stub test pins the status code; this one pins that the code is reached for the right
    reason — that the runner genuinely declines while a sweep is in flight.
    """
    release = asyncio.Event()

    async def sweep(progress: ReembedProgress) -> None:
        progress.total = 1
        await release.wait()

    runner = ReembedRunner(sweep)

    with _client(runner) as client:
        first = client.post("/api/internal/reembed", headers=HEADERS)
        second = client.post("/api/internal/reembed", headers=HEADERS)

        assert first.status_code == 202
        assert second.status_code == 409
        assert client.get("/api/internal/reembed/status", headers=HEADERS).json()["running"] is True

        release.set()
