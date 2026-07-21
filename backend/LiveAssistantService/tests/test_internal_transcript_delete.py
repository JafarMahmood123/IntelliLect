"""Internal transcript DELETE endpoints (session deletion + classroom deletion).

Offline, mirroring test_internal_transcripts: an InMemoryTranscriptRepository is seeded
and injected on app.state, exercised over an httpx AsyncClient on one event loop.
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
    monkeypatch.setenv("TRANSCRIPT_DB_URL", "")
    get_settings.cache_clear()
    yield
    get_settings.cache_clear()


def _final(text: str, start_ms: int, end_ms: int) -> TranscriptSegment:
    return TranscriptSegment(text, start_ms, end_ms, is_final=True, followed_by_pause=False)


async def _seed(repo, session_id, classroom_id, texts) -> None:
    await repo.ensure_session(session_id, classroom_id)
    for i, text in enumerate(texts):
        await repo.append_segment(session_id, _final(text, i * 1000, (i + 1) * 1000))
    await repo.finalize(session_id)


def _app_with_repo(repo) -> AsyncClient:
    app = create_app()
    app.state.transcript_repository = repo
    return AsyncClient(transport=ASGITransport(app=app), base_url="http://test")


async def test_delete_by_session_removes_transcript_and_returns_204():
    sid, cid = uuid4(), uuid4()
    repo = InMemoryTranscriptRepository()
    await _seed(repo, sid, cid, ["one", "two"])

    async with _app_with_repo(repo) as client:
        response = await client.delete(
            f"/api/internal/sessions/{sid}/transcript", headers=_HEADERS
        )

    assert response.status_code == 204
    assert await repo.get_session_transcript(sid) is None


async def test_delete_by_session_is_idempotent_for_unknown_session():
    repo = InMemoryTranscriptRepository()
    async with _app_with_repo(repo) as client:
        # Alternate path 6أ: no transcript to delete is still a success (not 404).
        response = await client.delete(
            f"/api/internal/sessions/{uuid4()}/transcript", headers=_HEADERS
        )
    assert response.status_code == 204


async def test_delete_by_session_requires_secret():
    repo = InMemoryTranscriptRepository()
    async with _app_with_repo(repo) as client:
        response = await client.delete(f"/api/internal/sessions/{uuid4()}/transcript")
    assert response.status_code == 401


async def test_delete_by_classroom_removes_only_that_classrooms_transcripts():
    target = uuid4()
    other = uuid4()
    s1, s2, s_other = uuid4(), uuid4(), uuid4()
    repo = InMemoryTranscriptRepository()
    await _seed(repo, s1, target, ["a"])
    await _seed(repo, s2, target, ["b"])
    await _seed(repo, s_other, other, ["c"])

    async with _app_with_repo(repo) as client:
        response = await client.delete(
            f"/api/internal/classrooms/{target}/transcripts", headers=_HEADERS
        )

    assert response.status_code == 200
    body = response.json()
    assert body["classroomId"] == str(target)
    assert body["transcriptsDeleted"] == 2
    # The other classroom's transcript survives.
    assert await repo.get_session_transcript(s_other) is not None
    assert await repo.get_session_transcript(s1) is None
