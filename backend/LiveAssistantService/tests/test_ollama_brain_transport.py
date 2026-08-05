"""The Ollama brain's HTTP half — the request it builds and how it fails.

`test_ollama_brain_client.py` stubs `_complete` and covers parsing; this covers everything that
stub replaces. Ollama is not what deploys (`BRAIN_PROVIDER=gemini` in both the compose file and
`.env.example`) but it is the documented way back to a local model, and it is the only brain that
works with no key and no internet — so it is what a fallback would run on, at exactly the moment
nobody wants to discover an untested path.

Two things differ from the Gemini client and are the reason this is not a copy of that file: the
core budget, which is what stops generation starving STT on the same 8-core host, and the fact
that its constrained decoding travels in `format` rather than in a generation config.
"""

from __future__ import annotations

from uuid import uuid4

import httpx
import pytest

from app.domain.evaluation.retrieved_chunk import RetrievedChunk
from app.domain.idea.boundary_trigger import BoundaryTrigger
from app.domain.idea.completed_idea import CompletedIdea
from app.infrastructure.brain.ollama_brain_client import (
    OllamaBrainClient,
    OllamaBrainError,
)
from app.infrastructure.config.settings import Settings

from tests.support.fake_http import FakeHttp, FakeResponse, Request


def _settings(**overrides) -> Settings:
    base = dict(
        brain_provider="ollama",
        ollama_base_url="http://ollama.example:11434",
        eval_model="qwen2.5:7b-instruct",
        eval_temperature=0.2,
        eval_max_tokens=512,
        eval_timeout_seconds=60.0,
        eval_num_thread=6,
        quiz_temperature=0.4,
        quiz_max_tokens=2048,
        quiz_timeout_seconds=90.0,
    )
    base.update(overrides)
    return Settings(**base)


def _reply(content: str) -> FakeResponse:
    return FakeResponse(200, {"message": {"content": content}})


def _client(monkeypatch, responder, **overrides):
    http = FakeHttp(responder=responder)
    monkeypatch.setattr(httpx, "AsyncClient", http.client_factory())
    return OllamaBrainClient(_settings(**overrides)), http


def _chunks(n: int = 2) -> list[RetrievedChunk]:
    return [
        RetrievedChunk(f"chunk {i}", 0.9 - i * 0.1, uuid4(), uuid4(), slide=i + 1)
        for i in range(n)
    ]


def _idea() -> CompletedIdea:
    return CompletedIdea("teacher explanation", 0, 1000, 1, BoundaryTrigger.PAUSE)


def _options(request: Request) -> dict:
    return request.json["options"]


def _messages(request: Request) -> list[dict]:
    return request.json["messages"]


# --- the request ---------------------------------------------------------------------------


async def test_the_system_prompt_is_a_system_message(monkeypatch):
    # Ollama's chat API takes roles, so the instruction is a system turn rather than something
    # prepended to the user's text where the model may treat it as content to discuss.
    client, http = _client(monkeypatch, lambda _r: _reply("{}"))

    await client.evaluate(_idea(), _chunks())

    roles = [m["role"] for m in _messages(http.requests[0])]
    assert roles == ["system", "user"]


async def test_streaming_is_off_so_the_reply_arrives_as_one_object(monkeypatch):
    # With streaming on, Ollama answers newline-delimited JSON and `response.json()` fails on the
    # second chunk — an error that reads like a malformed model reply rather than a request flag.
    client, http = _client(monkeypatch, lambda _r: _reply("{}"))

    await client.smoke_complete("anything")

    assert http.requests[0].json["stream"] is False


async def test_generation_is_bounded_to_a_core_budget(monkeypatch):
    """The lever that keeps the assistant usable on one machine.

    STT runs continuously during a session alongside generation. Unbounded, the model takes every
    core, the transcriber falls behind, and the feedback it produces is about something the class
    finished discussing a minute ago.
    """
    client, http = _client(monkeypatch, lambda _r: _reply("{}"), eval_num_thread=6)

    await client.evaluate(_idea(), _chunks())

    assert _options(http.requests[0])["num_thread"] == 6


async def test_a_zero_core_budget_means_ollama_s_default_not_a_zero_thread_request(monkeypatch):
    # 0 is documented as "let Ollama decide". Sending it literally would ask for no threads.
    client, http = _client(monkeypatch, lambda _r: _reply("{}"), eval_num_thread=0)

    await client.evaluate(_idea(), _chunks())

    assert "num_thread" not in _options(http.requests[0])


async def test_evaluation_uses_the_evaluation_caps(monkeypatch):
    client, http = _client(monkeypatch, lambda _r: _reply("{}"))

    await client.evaluate(_idea(), _chunks())

    options = _options(http.requests[0])
    assert options["temperature"] == 0.2
    assert options["num_predict"] == 512
    # No schema for an evaluation: the reply is one JSON object the parser is tolerant about,
    # and constraining it would cost generation time on the hot path.
    assert "format" not in http.requests[0].json


async def test_quiz_generation_gets_its_own_budget_and_its_schema(monkeypatch):
    # A truncated quiz is a lost quiz — the parser gets half an object and returns nothing, so
    # the teacher sees the button do nothing at all.
    client, http = _client(monkeypatch, lambda _r: _reply("{}"))

    await client.generate_quiz(
        "an idea", _chunks(), question_count=3, min_options=2, max_options=4
    )

    request = http.requests[0]
    assert _options(request)["num_predict"] == 2048
    assert _options(request)["temperature"] == 0.4
    # Ollama constrains decoding to a JSON Schema handed to `format` — lowercase, unlike Gemini's
    # proto-enum dialect. That difference is exactly why the canonical schema stays lowercase.
    assert request.json["format"]["type"] == "object"


async def test_quiz_generation_gets_the_longer_timeout(monkeypatch):
    client, http = _client(monkeypatch, lambda _r: _reply("{}"))

    await client.generate_quiz(
        "an idea", _chunks(), question_count=1, min_options=2, max_options=4
    )

    assert http.client_kwargs[0]["timeout"] == 90.0


async def test_writing_answers_sends_the_teacher_s_question_and_the_quiz_budget(monkeypatch):
    client, http = _client(monkeypatch, lambda _r: _reply("{}"))

    await client.generate_answers(
        "Why is the sky blue?", "an idea", _chunks(), min_options=2, max_options=4
    )

    request = http.requests[0]
    assert "Why is the sky blue?" in _messages(request)[1]["content"]
    assert _options(request)["num_predict"] == 2048
    assert request.json["format"]["type"] == "object"


async def test_an_auth_token_is_sent_as_a_bearer_header_when_one_is_configured(monkeypatch):
    # A local Ollama has no auth; a tunnelled or shared one does, and a dropped header is a 401
    # that reads like a broken model.
    client, http = _client(monkeypatch, lambda _r: _reply("{}"), ollama_auth_token="tunnel-token")

    await client.smoke_complete("anything")

    assert http.client_kwargs[0]["headers"] == {"Authorization": "Bearer tunnel-token"}


async def test_no_authorization_header_is_invented_when_no_token_is_set(monkeypatch):
    client, http = _client(monkeypatch, lambda _r: _reply("{}"))

    await client.smoke_complete("anything")

    assert http.client_kwargs[0]["headers"] == {}


# --- reading the reply ---------------------------------------------------------------------


async def test_a_reply_with_no_message_is_silence_rather_than_a_crash(monkeypatch):
    # Same degradation as the Gemini client: an empty completion reaches the parser and comes
    # back as "nothing to say", and the session carries on.
    client, _ = _client(monkeypatch, lambda _r: FakeResponse(200, {}))

    outcome = await client.evaluate(_idea(), _chunks())

    assert outcome.has_feedback is False


async def test_a_real_evaluation_is_parsed_through_the_transport(monkeypatch):
    content = (
        '{"has_feedback": true, "type": "gap", '
        '"suggestion": "The material also covers [1].", "citations": [1]}'
    )
    chunks = _chunks(2)
    client, _ = _client(monkeypatch, lambda _r: _reply(content))

    outcome = await client.evaluate(_idea(), chunks)

    assert outcome.has_feedback is True
    assert outcome.suggestion.sources == [chunks[0]]


# --- failure -------------------------------------------------------------------------------


async def test_an_unpulled_model_says_which_model_to_pull(monkeypatch):
    """404 is the most common way this fails and the easiest to misread.

    It looks like a wrong URL rather than a missing `ollama pull`, and sends the operator to
    check networking that is fine.
    """
    client, _ = _client(monkeypatch, lambda _r: FakeResponse(404, text="not found"))

    with pytest.raises(OllamaBrainError) as exc:
        await client.smoke_complete("anything")

    assert "ollama pull qwen2.5:7b-instruct" in str(exc.value)


async def test_an_unreachable_server_names_the_url_it_tried(monkeypatch):
    def _refuse(_request: Request):
        raise httpx.ConnectError("connection refused")

    client, _ = _client(monkeypatch, _refuse)

    with pytest.raises(OllamaBrainError) as exc:
        await client.evaluate(_idea(), _chunks())

    assert "ollama.example:11434" in str(exc.value)


async def test_any_other_http_failure_carries_the_status_and_the_body(monkeypatch):
    client, _ = _client(monkeypatch, lambda _r: FakeResponse(500, text="out of memory"))

    with pytest.raises(OllamaBrainError) as exc:
        await client.smoke_complete("anything")

    assert "500" in str(exc.value) and "out of memory" in str(exc.value)


async def test_a_timeout_is_raised_as_a_brain_error_for_the_caller_to_degrade(monkeypatch):
    # A local 7B model on CPU times out routinely under load; the pipeline handles a brain error
    # and moves to the next idea, where a bare httpx exception would stop the session.
    def _timeout(_request: Request):
        raise httpx.ReadTimeout("too slow")

    client, _ = _client(monkeypatch, _timeout)

    with pytest.raises(OllamaBrainError):
        await client.evaluate(_idea(), _chunks())
