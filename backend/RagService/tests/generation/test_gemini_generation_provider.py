"""Cover the Gemini generation provider: wire format, and every way it can return no text.

The happy path is one assertion. Everything else here is a failure mode that would otherwise
surface as an empty summary or a bare transport error — especially MAX_TOKENS, where a model
that spent its whole budget thinking looks identical to a model that had nothing to say.
"""

from __future__ import annotations

import json

import httpx
import pytest

from app.infrastructure.config.settings import Settings
from app.infrastructure.generation.gemini_generation_provider import (
    GeminiGenerationError,
    GeminiGenerationProvider,
)

_SETTINGS = Settings(
    gemini_api_key="test-key",
    gemini_base_url="https://gemini.test/v1beta",
    gemini_generation_model="gemini-flash-latest",
)


def _provider(handler, **kwargs) -> GeminiGenerationProvider:
    return GeminiGenerationProvider(
        _SETTINGS, transport=httpx.MockTransport(handler), **kwargs
    )


def _reply(text: str) -> dict:
    return {"candidates": [{"content": {"parts": [{"text": text}]}}]}


# --- happy path + wire format -------------------------------------------------------


@pytest.mark.asyncio
async def test_returns_the_models_text() -> None:
    provider = _provider(lambda _: httpx.Response(200, json=_reply("  A summary.  ")))
    assert await provider.generate("sys", "user") == "A summary."


@pytest.mark.asyncio
async def test_concatenates_multiple_parts() -> None:
    """Gemini may split a long answer across parts; joining them is not optional."""
    body = {"candidates": [{"content": {"parts": [{"text": "one "}, {"text": "two"}]}}]}
    provider = _provider(lambda _: httpx.Response(200, json=body))
    assert await provider.generate("sys", "user") == "one two"


@pytest.mark.asyncio
async def test_request_shape_and_auth() -> None:
    seen: dict = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["url"] = str(request.url)
        seen["key"] = request.headers.get("x-goog-api-key")
        seen["body"] = json.loads(request.content)
        return httpx.Response(200, json=_reply("ok"))

    await _provider(handler, model="custom-model", temperature=0.7, max_tokens=99).generate(
        "SYSTEM", "USER"
    )

    assert seen["url"] == "https://gemini.test/v1beta/models/custom-model:generateContent"
    # The key must be a header, never a query parameter, or it lands in request logs.
    assert seen["key"] == "test-key"
    assert "key=" not in seen["url"]
    assert seen["body"]["systemInstruction"] == {"parts": [{"text": "SYSTEM"}]}
    assert seen["body"]["contents"] == [{"role": "user", "parts": [{"text": "USER"}]}]
    assert seen["body"]["generationConfig"]["temperature"] == 0.7
    assert seen["body"]["generationConfig"]["maxOutputTokens"] == 99


@pytest.mark.asyncio
async def test_thinking_config_is_sent_when_set_and_omitted_when_blank() -> None:
    """Blank must OMIT the field: models before 3.x reject thinkingConfig outright."""
    seen: list[dict] = []

    def handler(request: httpx.Request) -> httpx.Response:
        seen.append(json.loads(request.content))
        return httpx.Response(200, json=_reply("ok"))

    await _provider(handler, thinking_level="low").generate("s", "u")
    assert seen[-1]["generationConfig"]["thinkingConfig"] == {"thinkingLevel": "low"}

    await _provider(handler, thinking_level="").generate("s", "u")
    assert "thinkingConfig" not in seen[-1]["generationConfig"]


# --- no-text failure modes ----------------------------------------------------------


@pytest.mark.asyncio
async def test_max_tokens_with_no_text_names_the_thinking_budget() -> None:
    """The trap: budget spent reasoning, zero prose. Must not read as "model said nothing"."""
    body = {"candidates": [{"finishReason": "MAX_TOKENS", "content": {"parts": []}}]}
    provider = _provider(lambda _: httpx.Response(200, json=body), max_tokens=64)

    with pytest.raises(GeminiGenerationError, match="Thinking tokens"):
        await provider.generate("sys", "user")


@pytest.mark.asyncio
async def test_no_candidates_reports_the_block_reason() -> None:
    body = {"candidates": [], "promptFeedback": {"blockReason": "SAFETY"}}
    provider = _provider(lambda _: httpx.Response(200, json=body))

    with pytest.raises(GeminiGenerationError, match="blockReason=SAFETY"):
        await provider.generate("sys", "user")


@pytest.mark.asyncio
async def test_empty_completion_reports_the_finish_reason() -> None:
    body = {"candidates": [{"finishReason": "RECITATION", "content": {"parts": []}}]}
    provider = _provider(lambda _: httpx.Response(200, json=body))

    with pytest.raises(GeminiGenerationError, match="RECITATION"):
        await provider.generate("sys", "user")


# --- transport / HTTP ---------------------------------------------------------------


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("status", "match"),
    [
        (401, "GEMINI_API_KEY"),
        (403, "GEMINI_API_KEY"),
        (404, "no model"),
        (429, "rate limit"),
        (500, "HTTP 500"),
    ],
)
async def test_http_errors_are_actionable(status: int, match: str) -> None:
    provider = _provider(lambda _: httpx.Response(status, text="detail"))
    with pytest.raises(GeminiGenerationError, match=match):
        await provider.generate("sys", "user")


@pytest.mark.asyncio
async def test_unreachable_api_names_the_base_url() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("no route", request=request)

    with pytest.raises(GeminiGenerationError, match="gemini.test"):
        await _provider(handler).generate("sys", "user")
