"""Real-API STT comparison — OPT-IN and skipped cleanly by default.

The offline tests pin the response *handling*; only this one can tell you whether Gemini
transcribes your audio ACCURATELY, and how its latency compares to Groq. That question cannot be
answered without a real clip and real keys, so it is opt-in rather than mocked.

Run it with:

    STT_TEST_WAV=/path/to/lecture.wav \\
    GEMINI_API_KEY=... GROQ_API_KEY=... \\
    .venv/bin/python -m pytest tests/test_gemini_stt_real.py -s -v

It PRINTS both transcripts and both latencies rather than asserting on wording — there is no
correct string to assert against, and the point is to let you read them side by side and decide
whether Gemini is good enough to switch to when Groq is blocked.
"""

from __future__ import annotations

import asyncio
import os
import time
import wave
from pathlib import Path

import numpy as np
import pytest

from app.infrastructure.config.settings import Settings
from app.infrastructure.stt.gemini_speech_to_text import GeminiSpeechToText
from app.infrastructure.stt.groq_speech_to_text import GroqSpeechToText

_FIXTURE_DIR = Path(__file__).parent / "fixtures"
_TARGET_RATE = 16000


def _find_fixture() -> Path | None:
    env = os.environ.get("STT_TEST_WAV")
    if env and Path(env).is_file():
        return Path(env)
    if _FIXTURE_DIR.is_dir():
        wavs = sorted(_FIXTURE_DIR.glob("*.wav"))
        if wavs:
            return wavs[0]
    return None


def _load_mono_16k(path: Path) -> np.ndarray:
    """Read a WAV to mono float32 at 16kHz, with nearest-neighbour resampling.

    Deliberately dependency-free (no soundfile/librosa): this is a diagnostic, and crude
    resampling is good enough to compare two engines on the same input.
    """
    with wave.open(str(path), "rb") as wav:
        channels = wav.getnchannels()
        width = wav.getsampwidth()
        rate = wav.getframerate()
        raw = wav.readframes(wav.getnframes())

    if width != 2:
        pytest.skip(f"{path.name} is {width * 8}-bit; this diagnostic expects 16-bit PCM")

    samples = np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0
    if channels > 1:
        samples = samples.reshape(-1, channels).mean(axis=1)
    if rate != _TARGET_RATE:
        idx = (np.arange(int(len(samples) * _TARGET_RATE / rate)) * rate / _TARGET_RATE)
        samples = samples[idx.astype(np.int64).clip(0, len(samples) - 1)]
    return samples


async def _timed(engine, samples: np.ndarray) -> tuple[str, float]:
    start = time.perf_counter()
    text = await engine._transcribe_window(samples, _TARGET_RATE)
    return text, time.perf_counter() - start


def test_compare_gemini_and_groq_on_a_real_clip():
    fixture = _find_fixture()
    if fixture is None:
        pytest.skip(
            "No audio fixture. Set STT_TEST_WAV=/path/to/clip.wav or drop a .wav in "
            "tests/fixtures/ (see that directory's README)."
        )
    gemini_key = os.environ.get("GEMINI_API_KEY", "")
    groq_key = os.environ.get("GROQ_API_KEY", "")
    if not gemini_key:
        pytest.skip("GEMINI_API_KEY not set — nothing to measure.")

    samples = _load_mono_16k(fixture)
    seconds = len(samples) / _TARGET_RATE
    print(f"\nclip: {fixture.name}  ({seconds:.1f}s of audio)")

    async def run() -> None:
        gemini = GeminiSpeechToText(
            Settings(
                stt_provider="gemini",
                gemini_api_key=gemini_key,
                stt_language=os.environ.get("STT_LANGUAGE", "en"),
            )
        )
        text, elapsed = await _timed(gemini, samples)
        print(f"\n── GEMINI ({elapsed:.2f}s, {seconds / elapsed:.1f}x realtime)\n{text!r}")

        if groq_key:
            groq = GroqSpeechToText(
                Settings(
                    stt_provider="groq",
                    groq_api_key=groq_key,
                    stt_language=os.environ.get("STT_LANGUAGE", "en"),
                )
            )
            try:
                text, elapsed = await _timed(groq, samples)
                print(f"\n── GROQ ({elapsed:.2f}s, {seconds / elapsed:.1f}x realtime)\n{text!r}")
            except Exception as exc:  # noqa: BLE001 — a blocked exit IP is the expected case here
                print(f"\n── GROQ unavailable: {type(exc).__name__}: {str(exc)[:200]}")
        else:
            print("\n── GROQ skipped (GROQ_API_KEY not set)")

    asyncio.run(run())
