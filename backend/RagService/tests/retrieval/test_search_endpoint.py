from __future__ import annotations

from uuid import uuid4

from fastapi.testclient import TestClient

from app.api.dependencies import get_chunk_repository, get_embedding_provider
from app.api.main import create_app
from app.infrastructure.config.settings import Settings

from tests.retrieval.fakes import FakeChunkRepository, FakeEmbeddingProvider, make_result

HEADERS = {"X-Internal-Secret": "test-internal-secret"}
DIM = Settings().embedding_dim


def _client(results=None) -> TestClient:
    app = create_app()
    repo = FakeChunkRepository(results if results is not None else [make_result(0.9)])
    app.dependency_overrides[get_embedding_provider] = lambda: FakeEmbeddingProvider(DIM)
    app.dependency_overrides[get_chunk_repository] = lambda: repo
    return TestClient(app)


def test_valid_search_returns_200_with_results() -> None:
    results = [make_result(0.95, chunk_index=3, page=2), make_result(0.80, chunk_index=7)]
    with _client(results) as client:
        response = client.post(
            "/api/search",
            json={"classroomId": str(uuid4()), "query": "explain gravity", "topK": 5},
            headers=HEADERS,
        )

    assert response.status_code == 200
    body = response.json()
    assert len(body["results"]) == 2
    first = body["results"][0]
    # Response uses camelCase aliases and preserves best-first order.
    assert first["score"] == 0.95
    assert first["chunkIndex"] == 3
    assert first["metadata"] == {"page": 2}
    assert "chunkId" in first and "documentId" in first and "text" in first


def test_missing_query_returns_422() -> None:
    with _client() as client:
        response = client.post(
            "/api/search",
            json={"classroomId": str(uuid4())},
            headers=HEADERS,
        )
    assert response.status_code == 422


def test_empty_query_returns_422() -> None:
    with _client() as client:
        response = client.post(
            "/api/search",
            json={"classroomId": str(uuid4()), "query": "   "},
            headers=HEADERS,
        )
    assert response.status_code == 422


def test_invalid_classroom_id_returns_422() -> None:
    with _client() as client:
        response = client.post(
            "/api/search",
            json={"classroomId": "not-a-uuid", "query": "hello"},
            headers=HEADERS,
        )
    assert response.status_code == 422


def test_missing_secret_is_unauthorized() -> None:
    with _client() as client:
        response = client.post(
            "/api/search",
            json={"classroomId": str(uuid4()), "query": "hello"},
        )
    assert response.status_code == 401


def test_wrong_secret_is_unauthorized() -> None:
    with _client() as client:
        response = client.post(
            "/api/search",
            json={"classroomId": str(uuid4()), "query": "hello"},
            headers={"X-Internal-Secret": "nope"},
        )
    assert response.status_code == 401
