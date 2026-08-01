"""Internal quiz-generation endpoint: auth, and the status codes the caller acts on.

Offline: the generator is overridden via FastAPI's dependency_overrides, so no brain and no
KnowledgeService are involved. What matters here is that "the lecture has not said enough yet" and
"the assistant broke" stay DIFFERENT statuses — ClassroomService turns them into different
messages, and the fix a teacher needs differs between them.
"""

from __future__ import annotations

from uuid import uuid4

import pytest
from httpx import ASGITransport, AsyncClient

from app.api.dependencies import get_quiz_generator
from app.api.main import create_app
from app.application.services.quiz_generator import NoIdeaAvailable, QuizGenerationFailed
from app.domain.quiz.generated_quiz import GeneratedOption, GeneratedQuestion, GeneratedQuiz
from app.infrastructure.config.settings import get_settings

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


class StubGenerator:
    def __init__(self, quiz=None, *, error: Exception | None = None):
        self._quiz = quiz
        self._error = error
        self.calls: list[dict] = []

    async def generate(self, session_id, classroom_id, **kwargs):
        self.calls.append({"session_id": session_id, "classroom_id": classroom_id, **kwargs})
        if self._error is not None:
            raise self._error
        return self._quiz


def _quiz() -> GeneratedQuiz:
    return GeneratedQuiz(
        title="Caching",
        questions=[
            GeneratedQuestion("What is a cache miss?", [
                GeneratedOption("Not in the cache", True),
                GeneratedOption("In the cache", False),
            ])
        ],
        citations=[1],
    )


def _client(generator) -> AsyncClient:
    app = create_app()
    app.dependency_overrides[get_quiz_generator] = lambda: generator
    return AsyncClient(transport=ASGITransport(app=app), base_url="http://test")


def _body(**overrides) -> dict:
    return {"classroomId": str(uuid4()), "questionCount": 3, "minOptions": 2, "maxOptions": 4} | overrides


async def test_returns_the_generated_questions():
    generator = StubGenerator(_quiz())
    session_id = uuid4()

    async with _client(generator) as client:
        response = await client.post(
            f"/api/internal/sessions/{session_id}/quiz", json=_body(), headers=_HEADERS
        )

    assert response.status_code == 200
    body = response.json()
    assert body["title"] == "Caching"
    assert body["grounded"] is True
    assert body["questions"][0]["options"][0]["isCorrect"] is True


async def test_bounds_are_passed_through_to_the_generator():
    """They come from ClassroomService, which owns the quiz limits — this service must not
    substitute its own."""
    generator = StubGenerator(_quiz())

    async with _client(generator) as client:
        await client.post(
            f"/api/internal/sessions/{uuid4()}/quiz",
            json=_body(questionCount=7, minOptions=3, maxOptions=5),
            headers=_HEADERS,
        )

    assert generator.calls[0]["question_count"] == 7
    assert generator.calls[0]["min_options"] == 3
    assert generator.calls[0]["max_options"] == 5


async def test_no_idea_yet_is_409():
    generator = StubGenerator(error=NoIdeaAvailable("nothing transcribed yet"))

    async with _client(generator) as client:
        response = await client.post(
            f"/api/internal/sessions/{uuid4()}/quiz", json=_body(), headers=_HEADERS
        )

    assert response.status_code == 409


async def test_generation_failure_is_503():
    generator = StubGenerator(error=QuizGenerationFailed("brain unreachable"))

    async with _client(generator) as client:
        response = await client.post(
            f"/api/internal/sessions/{uuid4()}/quiz", json=_body(), headers=_HEADERS
        )

    assert response.status_code == 503


async def test_contradictory_option_bounds_are_rejected():
    generator = StubGenerator(_quiz())

    async with _client(generator) as client:
        response = await client.post(
            f"/api/internal/sessions/{uuid4()}/quiz",
            json=_body(minOptions=5, maxOptions=3),
            headers=_HEADERS,
        )

    assert response.status_code == 422
    assert generator.calls == []


async def test_requires_the_internal_secret():
    generator = StubGenerator(_quiz())

    async with _client(generator) as client:
        response = await client.post(
            f"/api/internal/sessions/{uuid4()}/quiz", json=_body()
        )

    assert response.status_code == 401
    assert generator.calls == []


async def test_wrong_internal_secret_is_rejected():
    generator = StubGenerator(_quiz())

    async with _client(generator) as client:
        response = await client.post(
            f"/api/internal/sessions/{uuid4()}/quiz",
            json=_body(),
            headers={"X-Internal-Secret": "not-the-secret"},
        )

    assert response.status_code == 401
    assert generator.calls == []
