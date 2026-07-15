"""Internal transcript endpoint (S-0): assembled transcript, auth, 404.

Offline: an InMemoryTranscriptRepository is pre-populated and set on app.state (the
same seam the lifespan uses). An httpx AsyncClient over the ASGI app keeps everything
on one event loop so the repository's async lock is never crossed between loops.
"""

from __future__ import annotations

from uuid import uuid4

import pytest
from httpx import ASGITransport, AsyncClient

from app.api.main import create_app
from app.domain.transcript.transcript_segment import TranscriptSegment
from app.infrastructure.config.settings import get_settings
from app.infrastructure.persistence.in_memory_transcript_repository import (
    InMemoryTranscriptRepository,
)

_SECRET = "test-internal-secret"
_HEADERS = {"X-Internal-Secret": _SECRET}


@pytest.fixture(autouse=True)
def _internal_secret_env(monkeypatch):
    monkeypatch.setenv("INTERNAL_API_SECRET", _SECRET)
    monkeypatch.setenv("OLLAMA_BASE_URL", "")
    monkeypatch.setenv("KNOWLEDGE_BASE_URL", "")
    monkeypatch.setenv("TRANSCRIPT_DB_URL", "")  # in-memory store
    get_settings.cache_clear()
    yield
    get_settings.cache_clear()


def _final(text: str, start_ms: int, end_ms: int) -> TranscriptSegment:
    return TranscriptSegment(text, start_ms, end_ms, is_final=True, followed_by_pause=False)


async def _seeded_repo(session_id, classroom_id, texts) -> InMemoryTranscriptRepository:
    repo = InMemoryTranscriptRepository()
    await repo.ensure_session(session_id, classroom_id)
    for i, text in enumerate(texts):
        await repo.append_segment(session_id, _final(text, i * 1000, (i + 1) * 1000))
    await repo.finalize(session_id)
    return repo


def _app_with_repo(repo) -> AsyncClient:
    app = create_app()
    app.state.transcript_repository = repo  # bypass lifespan; inject the seeded store
    return AsyncClient(transport=ASGITransport(app=app), base_url="http://test")


async def test_returns_assembled_transcript_for_known_session():
    sid, cid = uuid4(), uuid4()
    repo = await _seeded_repo(sid, cid, ["one two", "three four", "five"])

    async with _app_with_repo(repo) as client:
        response = await client.get(
            f"/api/internal/sessions/{sid}/transcript", headers=_HEADERS
        )

    assert response.status_code == 200
    body = response.json()
    assert body["sessionId"] == str(sid)
    assert body["classroomId"] == str(cid)
    assert body["status"] == "Finalized"
    assert body["segmentCount"] == 3
    assert body["text"] == "one two three four five"


async def test_unknown_session_is_404():
    repo = InMemoryTranscriptRepository()
    async with _app_with_repo(repo) as client:
        response = await client.get(
            f"/api/internal/sessions/{uuid4()}/transcript", headers=_HEADERS
        )
    assert response.status_code == 404


async def test_missing_secret_is_unauthorized():
    sid, cid = uuid4(), uuid4()
    repo = await _seeded_repo(sid, cid, ["hello"])
    async with _app_with_repo(repo) as client:
        response = await client.get(f"/api/internal/sessions/{sid}/transcript")
    assert response.status_code == 401


async def test_wrong_secret_is_unauthorized():
    sid, cid = uuid4(), uuid4()
    repo = await _seeded_repo(sid, cid, ["hello"])
    async with _app_with_repo(repo) as client:
        response = await client.get(
            f"/api/internal/sessions/{sid}/transcript",
            headers={"X-Internal-Secret": "nope"},
        )
    assert response.status_code == 401
