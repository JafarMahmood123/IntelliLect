"""OFFLINE tests for RagRetrievalClient using httpx.MockTransport.

Asserts the exact request sent to RagService (URL / method / auth header /
JSON body) and the mapping of the response to RetrievedChunk — no live service.
"""

from __future__ import annotations

import json
from uuid import uuid4

import httpx
import pytest

from app.infrastructure.config.settings import Settings
from app.infrastructure.retrieval.rag_retrieval_client import (
    RagRetrievalClient,
    RetrievalError,
)


def _settings() -> Settings:
    return Settings(knowledge_base_url="http://rag-service:8080", internal_api_secret="s3cret")


def _client(handler) -> RagRetrievalClient:
    return RagRetrievalClient(_settings(), transport=httpx.MockTransport(handler))


async def test_posts_to_search_with_secret_and_body_and_maps_results():
    captured = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["request"] = request
        captured["body"] = json.loads(request.content)
        return httpx.Response(
            200,
            json={
                "results": [
                    {
                        "chunkId": str(uuid4()), "documentId": str(uuid4()),
                        "text": "gravity bends spacetime", "score": 0.91,
                        "chunkIndex": 3, "metadata": {"page": 2},
                    },
                    {
                        "chunkId": str(uuid4()), "documentId": str(uuid4()),
                        "text": "orbit basics", "score": 0.7,
                        "chunkIndex": 7, "metadata": {"slide": 5},
                    },
                ]
            },
        )

    classroom_id = uuid4()
    chunks = await _client(handler).retrieve(classroom_id, "explain gravity", 5)

    request = captured["request"]
    assert request.method == "POST"
    assert str(request.url) == "http://rag-service:8080/api/search"
    assert request.headers["X-Internal-Secret"] == "s3cret"
    assert captured["body"] == {
        "classroomId": str(classroom_id), "query": "explain gravity", "topK": 5,
    }

    assert [c.text for c in chunks] == ["gravity bends spacetime", "orbit basics"]
    assert chunks[0].score == 0.91 and chunks[0].page == 2 and chunks[0].slide is None
    assert chunks[1].slide == 5 and chunks[1].page is None


async def test_missing_metadata_maps_to_none_locations():
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"results": [
            {"chunkId": str(uuid4()), "documentId": str(uuid4()), "text": "t", "score": 0.5}
        ]})

    chunks = await _client(handler).retrieve(uuid4(), "q", 3)

    assert chunks[0].page is None and chunks[0].slide is None and chunks[0].section is None


async def test_empty_results_returns_empty_list():
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"results": []})

    assert await _client(handler).retrieve(uuid4(), "q", 3) == []


async def test_unauthorized_raises_retrieval_error():
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(401, text="unauthorized")

    with pytest.raises(RetrievalError):
        await _client(handler).retrieve(uuid4(), "q", 3)


async def test_server_error_raises_retrieval_error():
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(500, text="boom")

    with pytest.raises(RetrievalError):
        await _client(handler).retrieve(uuid4(), "q", 3)


async def test_transport_error_raises_retrieval_error():
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("connection refused", request=request)

    with pytest.raises(RetrievalError):
        await _client(handler).retrieve(uuid4(), "q", 3)
