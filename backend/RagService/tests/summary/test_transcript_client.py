"""LiveAssistantTranscriptClient (S-1) — URL, auth header, mapping, error handling.

Offline: httpx.MockTransport intercepts the request so no LiveAssistantService is
needed. Asserts the client hits the S-0 internal endpoint with the internal secret and
maps the JSON payload into a TranscriptDocument.
"""

from __future__ import annotations

from uuid import uuid4

import httpx
import pytest

from app.infrastructure.config.settings import Settings
from app.infrastructure.live_assistant.transcript_client import (
    LiveAssistantTranscriptClient,
    TranscriptFetchError,
)

_BASE = "http://live-assistant:8080"
_SECRET = "shared-secret"


def _settings(**overrides) -> Settings:
    return Settings(
        live_assistant_base_url=_BASE,
        internal_api_secret=_SECRET,
        **overrides,
    )


async def test_fetches_correct_url_with_auth_and_maps_payload():
    session_id = uuid4()
    classroom_id = uuid4()
    captured: dict = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["url"] = str(request.url)
        captured["secret"] = request.headers.get("X-Internal-Secret")
        return httpx.Response(
            200,
            json={
                "sessionId": str(session_id),
                "classroomId": str(classroom_id),
                "status": "Finalized",
                "segmentCount": 3,
                "text": "one two three",
            },
        )

    client = LiveAssistantTranscriptClient(
        _settings(), transport=httpx.MockTransport(handler)
    )
    document = await client.fetch(session_id)

    assert captured["url"] == f"{_BASE}/api/internal/sessions/{session_id}/transcript"
    assert captured["secret"] == _SECRET
    assert document.session_id == session_id
    assert document.classroom_id == classroom_id
    assert document.status == "Finalized"
    assert document.segment_count == 3
    assert document.text == "one two three"


async def test_unknown_session_raises_transcript_fetch_error():
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(404, json={"detail": "unknown"})

    client = LiveAssistantTranscriptClient(
        _settings(), transport=httpx.MockTransport(handler)
    )
    with pytest.raises(TranscriptFetchError):
        await client.fetch(uuid4())


async def test_server_error_raises_transcript_fetch_error():
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(500, text="boom")

    client = LiveAssistantTranscriptClient(
        _settings(), transport=httpx.MockTransport(handler)
    )
    with pytest.raises(TranscriptFetchError):
        await client.fetch(uuid4())


async def test_malformed_payload_raises_transcript_fetch_error():
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"sessionId": "not-a-uuid"})

    client = LiveAssistantTranscriptClient(
        _settings(), transport=httpx.MockTransport(handler)
    )
    with pytest.raises(TranscriptFetchError):
        await client.fetch(uuid4())


async def test_missing_base_url_raises_before_any_request():
    client = LiveAssistantTranscriptClient(
        Settings(live_assistant_base_url="", internal_api_secret=_SECRET)
    )
    with pytest.raises(TranscriptFetchError):
        await client.fetch(uuid4())
