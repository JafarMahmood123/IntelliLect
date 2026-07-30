"""GeminiSpeechToText contract, with the HTTP transport stubbed (no network, no key).

The interesting risk here is NOT the transport — it is that this engine is a general-purpose LLM
being asked to behave like an ASR model. A dedicated ASR model returns "" for silence; an LLM
narrates ("There is no speech in this audio"), wraps output in markdown, or prepends "Here is the
transcription:". Any of those reaching the boundary detector would look like the teacher having
actually said those words, so ``_clean_reply`` is what these tests mostly pin down.

The mirror-image risk is OVER-filtering: a teacher who genuinely says "there is no audio here"
must still be transcribed. That is why the no-speech list is matched only against a whole
normalized reply, and why there is a test for it.
"""

from __future__ import annotations

import base64
import json

import httpx
import numpy as np
import pytest

from app.infrastructure.config.settings import Settings
from app.infrastructure.stt.gemini_speech_to_text import (
    GeminiSpeechToText,
    GeminiSpeechToTextError,
    _build_prompt,
    _clean_reply,
)


def _settings(**overrides) -> Settings:
    base = dict(
        stt_provider="gemini",
        gemini_api_key="test-key",
        gemini_stt_model="gemini-flash-latest",
        stt_language="en",
        stt_initial_prompt="Gemini, LiveKit, IntelliLect",
        gemini_stt_timeout_seconds=5.0,
    )
    base.update(overrides)
    return Settings(**base)


def _patch(monkeypatch, handler) -> dict:
    captured: dict = {}

    def _h(request: httpx.Request) -> httpx.Response:
        captured["url"] = str(request.url)
        captured["headers"] = dict(request.headers)
        captured["body"] = request.content
        return handler(request)

    real = httpx.AsyncClient

    def _factory(*a, **kw):
        kw["transport"] = httpx.MockTransport(_h)
        return real(*a, **kw)

    monkeypatch.setattr(
        "app.infrastructure.stt.gemini_speech_to_text.httpx.AsyncClient", _factory
    )
    return captured


def _reply(text: str, status: int = 200, **extra) -> httpx.Response:
    candidate = {"content": {"parts": [{"text": text}]}}
    candidate.update(extra)
    return httpx.Response(status, json={"candidates": [candidate]})


# --- _clean_reply: the LLM-narration filter -------------------------------------------------


@pytest.mark.parametrize(
    "raw",
    [
        "<NO_SPEECH>",
        "  <NO_SPEECH>  ",
        "<no_speech>",
        "No speech detected.",
        "There is no speech in this audio.",
        "Silence",
        "(inaudible)",
        "N/A",
        "none",
        '"<NO_SPEECH>"',
        "```\n<NO_SPEECH>\n```",
    ],
)
def test_no_speech_replies_become_empty(raw):
    """These must all yield '' so StreamingTranscriber drops the window entirely."""
    assert _clean_reply(raw) == ""


@pytest.mark.parametrize(
    "raw,expected",
    [
        ("Here is the transcription: the customer app sends type one.",
         "the customer app sends type one."),
        ("Transcript: we post to the device tokens endpoint.",
         "we post to the device tokens endpoint."),
        ("```\nthe owner app sends recipient type two\n```",
         "the owner app sends recipient type two"),
        ("```text\nthe owner app sends recipient type two\n```",
         "the owner app sends recipient type two"),
        ('"we register the token after login"', "we register the token after login"),
        ("  plain text with spaces  ", "plain text with spaces"),
    ],
)
def test_wrapping_and_preambles_are_stripped(raw, expected):
    assert _clean_reply(raw) == expected


@pytest.mark.parametrize(
    "raw",
    [
        "There is no speech in this audio, which is why we need a fallback path.",
        "No speech detected is the message the API returns when the mic is muted.",
        "Silence is an important part of teaching, so use it deliberately.",
    ],
)
def test_real_speech_that_merely_mentions_silence_is_kept(raw):
    """Guards against over-filtering: the no-speech list is a WHOLE-reply match, not a substring."""
    assert _clean_reply(raw) == raw


def test_transcript_is_not_otherwise_altered():
    verbatim = "so, uh, the the customer app sends recipient type one and then we"
    assert _clean_reply(verbatim) == verbatim


# --- prompt construction --------------------------------------------------------------------


def test_prompt_pins_language_when_configured():
    prompt = _build_prompt("en", "")
    assert "expected to be in en" in prompt
    assert "do NOT translate" in prompt


def test_blank_language_asks_for_the_spoken_language():
    """Empty STT_LANGUAGE is the multilingual mode — this is what makes Arabic possible."""
    prompt = _build_prompt("", "")
    assert "whatever language is actually spoken" in prompt
    assert "expected to be in" not in prompt


def test_vocabulary_is_included_only_when_supplied():
    assert "IntelliLect" in _build_prompt("en", "IntelliLect, LiveKit")
    assert "Domain vocabulary" not in _build_prompt("en", "")


# --- transport ------------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_window_is_sent_as_base64_wav_inline_audio(monkeypatch):
    captured = _patch(monkeypatch, lambda r: _reply("hello there"))
    engine = GeminiSpeechToText(_settings())

    text = await engine._transcribe_window(
        np.array([0.0, 0.25, -0.25], dtype=np.float32), 16000
    )

    assert text == "hello there"
    body = json.loads(captured["body"])
    parts = body["contents"][0]["parts"]
    inline = next(p["inlineData"] for p in parts if "inlineData" in p)
    assert inline["mimeType"] == "audio/wav"
    # Must be a real RIFF/WAVE container, not raw float32 — the API rejects the latter.
    assert base64.b64decode(inline["data"])[:4] == b"RIFF"
    assert "gemini-flash-latest:generateContent" in captured["url"]


@pytest.mark.asyncio
async def test_api_key_travels_in_a_header_not_the_url(monkeypatch):
    captured = _patch(monkeypatch, lambda r: _reply("ok"))
    await GeminiSpeechToText(_settings())._transcribe_window(
        np.zeros(4, dtype=np.float32), 16000
    )
    assert captured["headers"]["x-goog-api-key"] == "test-key"
    assert "test-key" not in captured["url"]


@pytest.mark.asyncio
async def test_temperature_is_zero_so_it_does_not_paraphrase(monkeypatch):
    captured = _patch(monkeypatch, lambda r: _reply("ok"))
    await GeminiSpeechToText(_settings())._transcribe_window(
        np.zeros(4, dtype=np.float32), 16000
    )
    config = json.loads(captured["body"])["generationConfig"]
    assert config["temperature"] == 0.0
    assert config["thinkingConfig"] == {"thinkingLevel": "low"}


@pytest.mark.asyncio
async def test_blank_thinking_level_omits_the_field(monkeypatch):
    """Non-3.x models reject thinkingConfig outright, so it must be omittable."""
    captured = _patch(monkeypatch, lambda r: _reply("ok"))
    await GeminiSpeechToText(_settings(gemini_stt_thinking_level=""))._transcribe_window(
        np.zeros(4, dtype=np.float32), 16000
    )
    assert "thinkingConfig" not in json.loads(captured["body"])["generationConfig"]


@pytest.mark.asyncio
async def test_blocked_or_empty_candidates_degrade_to_no_transcript(monkeypatch):
    """Losing one window is survivable; raising would end the whole session."""
    _patch(monkeypatch, lambda r: httpx.Response(200, json={"candidates": []}))
    assert (
        await GeminiSpeechToText(_settings())._transcribe_window(
            np.zeros(4, dtype=np.float32), 16000
        )
        == ""
    )


@pytest.mark.asyncio
@pytest.mark.parametrize("status", [401, 403])
async def test_auth_failures_name_the_key(monkeypatch, status):
    _patch(monkeypatch, lambda r: httpx.Response(status, text="denied"))
    with pytest.raises(GeminiSpeechToTextError, match="GEMINI_API_KEY"):
        await GeminiSpeechToText(_settings())._transcribe_window(
            np.zeros(4, dtype=np.float32), 16000
        )


@pytest.mark.asyncio
async def test_rate_limit_explains_that_audio_burns_quota(monkeypatch):
    _patch(monkeypatch, lambda r: httpx.Response(429, text="quota"))
    with pytest.raises(GeminiSpeechToTextError, match="429"):
        await GeminiSpeechToText(_settings())._transcribe_window(
            np.zeros(4, dtype=np.float32), 16000
        )


@pytest.mark.asyncio
async def test_transport_error_names_the_base_url(monkeypatch):
    def _boom(request):
        raise httpx.ConnectError("no route")

    _patch(monkeypatch, _boom)
    with pytest.raises(GeminiSpeechToTextError, match="GEMINI_BASE_URL"):
        await GeminiSpeechToText(_settings())._transcribe_window(
            np.zeros(4, dtype=np.float32), 16000
        )


@pytest.mark.asyncio
async def test_narrated_silence_from_the_live_shaped_response_is_dropped(monkeypatch):
    """End to end through the transport: an LLM narration must not become transcript text."""
    _patch(monkeypatch, lambda r: _reply("There is no speech in this audio."))
    assert (
        await GeminiSpeechToText(_settings())._transcribe_window(
            np.zeros(4, dtype=np.float32), 16000
        )
        == ""
    )
