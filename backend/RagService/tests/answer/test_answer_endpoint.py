from __future__ import annotations

from uuid import uuid4

from fastapi.testclient import TestClient

from app.api.dependencies import (
    get_chunk_repository,
    get_embedding_provider,
    get_generation_provider,
)
from app.api.main import create_app
from app.infrastructure.config.settings import Settings

from tests.answer.fakes import FakeGenerationProvider
from tests.retrieval.fakes import FakeChunkRepository, FakeEmbeddingProvider, make_result

HEADERS = {"X-Internal-Secret": "test-internal-secret"}
DIM = Settings().embedding_dim


def _client(results=None, answer="Grounded answer [1].") -> TestClient:
    app = create_app()
    repo = FakeChunkRepository(results if results is not None else [make_result(0.9, page=1)])
    app.dependency_overrides[get_embedding_provider] = lambda: FakeEmbeddingProvider(DIM)
    app.dependency_overrides[get_chunk_repository] = lambda: repo
    app.dependency_overrides[get_generation_provider] = lambda: FakeGenerationProvider(answer)
    return TestClient(app)


def test_valid_answer_returns_200_with_answer_and_sources() -> None:
    results = [make_result(0.95, chunk_index=2, page=3), make_result(0.80, chunk_index=7, slide=1)]
    with _client(results, answer="It works [1][2].") as client:
        response = client.post(
            "/api/answer",
            json={"classroomId": str(uuid4()), "question": "explain gravity", "topK": 6},
            headers=HEADERS,
        )

    assert response.status_code == 200
    body = response.json()
    assert body["answer"] == "It works [1][2]."
    assert len(body["sources"]) == 2
    first = body["sources"][0]
    assert first["citation"] == 1
    assert "chunkId" in first and "documentId" in first and "score" in first
    assert first["page"] == 3


def test_no_results_returns_200_with_no_context_answer() -> None:
    with _client(results=[]) as client:
        response = client.post(
            "/api/answer",
            json={"classroomId": str(uuid4()), "question": "anything"},
            headers=HEADERS,
        )
    assert response.status_code == 200
    assert response.json()["sources"] == []


def test_missing_question_returns_422() -> None:
    with _client() as client:
        response = client.post(
            "/api/answer", json={"classroomId": str(uuid4())}, headers=HEADERS
        )
    assert response.status_code == 422


def test_empty_question_returns_422() -> None:
    with _client() as client:
        response = client.post(
            "/api/answer",
            json={"classroomId": str(uuid4()), "question": "   "},
            headers=HEADERS,
        )
    assert response.status_code == 422


def test_missing_secret_is_unauthorized() -> None:
    with _client() as client:
        response = client.post(
            "/api/answer", json={"classroomId": str(uuid4()), "question": "hi"}
        )
    assert response.status_code == 401


def test_wrong_secret_is_unauthorized() -> None:
    with _client() as client:
        response = client.post(
            "/api/answer",
            json={"classroomId": str(uuid4()), "question": "hi"},
            headers={"X-Internal-Secret": "nope"},
        )
    assert response.status_code == 401
