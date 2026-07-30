"""GroqSpeechToText contract, with the HTTP transport stubbed (no network, no key).

Two things are worth pinning. The window must arrive as a REAL wav container (the API rejects raw
float32), and a 403 must carry the blocked-exit-IP hint — Groq blocks VPN/datacenter ranges, and
the failure is about where the request came from, not what was in it, so a generic HTTP error
sends the operator looking at the audio or the key instead of the route.
"""

from __future__ import annotations

import io
import wave

import httpx
import numpy as np
import pytest

from app.infrastructure.config.settings import Settings
from app.infrastructure.stt.groq_speech_to_text import (
    GroqSpeechToText,
    GroqSpeechToTextError,
)
from app.infrastructure.stt.wav_encoding import to_wav_bytes as _to_wav_bytes


def _settings(**overrides) -> Settings:
    return Settings(
        stt_provider="groq",
        groq_api_key=overrides.get("groq_api_key", "gsk_test"),
        groq_stt_model=overrides.get("groq_stt_model", "whisper-large-v3-turbo"),
        stt_language=overrides.get("stt_language", "en"),
        stt_initial_prompt=overrides.get("stt_initial_prompt", "Gemini, LiveKit"),
        groq_timeout_seconds=5.0,
    )


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
        "app.infrastructure.stt.groq_speech_to_text.httpx.AsyncClient", _factory
    )
    return captured


def test_window_is_encoded_as_real_16bit_wav():
    samples = np.array([0.0, 0.5, -0.5, 1.0, -1.0], dtype=np.float32)

    data = _to_wav_bytes(samples, 16000)

    with wave.open(io.BytesIO(data), "rb") as wav:
        assert wav.getnchannels() == 1
        assert wav.getsampwidth() == 2  # 16-bit
        assert wav.getframerate() == 16000
        assert wav.getnframes() == 5
        pcm = np.frombuffer(wav.readframes(5), dtype=np.int16)
    # Full-scale input must not wrap around into noise.
    assert pcm[3] == 32767 and pcm[4] == -32767


def test_out_of_range_samples_are_clipped_not_wrapped():
    data = _to_wav_bytes(np.array([2.5, -2.5], dtype=np.float32), 16000)

    with wave.open(io.BytesIO(data), "rb") as wav:
        pcm = np.frombuffer(wav.readframes(2), dtype=np.int16)
    assert pcm[0] == 32767 and pcm[1] == -32767


@pytest.mark.asyncio
async def test_successful_transcription_returns_stripped_text(monkeypatch):
    captured = _patch(
        monkeypatch, lambda _r: httpx.Response(200, json={"text": "  hello world  "})
    )

    text = await GroqSpeechToText(_settings())._transcribe_window(
        np.zeros(16000, dtype=np.float32), 16000
    )

    assert text == "hello world"
    assert captured["headers"]["authorization"] == "Bearer gsk_test"
    assert "audio/transcriptions" in captured["url"]
    body = captured["body"]
    assert b"whisper-large-v3-turbo" in body
    assert b"RIFF" in body  # a real wav container was uploaded
    assert b"Gemini, LiveKit" in body  # initial prompt forwarded
    assert b'name="language"' in body


@pytest.mark.asyncio
async def test_403_names_the_blocked_exit_ip(monkeypatch):
    _patch(monkeypatch, lambda _r: httpx.Response(403, text="Forbidden"))

    with pytest.raises(GroqSpeechToTextError) as err:
        await GroqSpeechToText(_settings())._transcribe_window(
            np.zeros(16000, dtype=np.float32), 16000
        )

    message = str(err.value).lower()
    assert "403" in message
    assert "vpn" in message, "a 403 must point at the exit IP, not the request contents"


@pytest.mark.parametrize(
    ("status", "needle"),
    [(401, "key"), (429, "rate limit"), (500, "500")],
)
@pytest.mark.asyncio
async def test_error_statuses_raise_with_actionable_text(monkeypatch, status, needle):
    _patch(monkeypatch, lambda _r: httpx.Response(status, text="nope"))

    with pytest.raises(GroqSpeechToTextError) as err:
        await GroqSpeechToText(_settings())._transcribe_window(
            np.zeros(16000, dtype=np.float32), 16000
        )

    assert needle in str(err.value).lower()


@pytest.mark.asyncio
async def test_transport_error_raises(monkeypatch):
    def _boom(_request):
        raise httpx.ConnectError("no route")

    _patch(monkeypatch, _boom)

    with pytest.raises(GroqSpeechToTextError):
        await GroqSpeechToText(_settings())._transcribe_window(
            np.zeros(16000, dtype=np.float32), 16000
        )
