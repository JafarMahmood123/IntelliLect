"""Real-model STT smoke test — OPT-IN and skipped cleanly by default.

It runs only when BOTH are true:
  * faster-whisper (and CTranslate2) are installed, and
  * an English WAV fixture is available — via the ``STT_TEST_WAV`` env var or a file
    dropped in ``tests/fixtures/`` (e.g. english_sample.wav).

We deliberately do NOT commit an audio clip or add a TTS dependency, so CI skips this
with a clear message. When enabled it downloads the configured model on first run.
"""

from __future__ import annotations

import os
from pathlib import Path

import pytest

from app.infrastructure.audio.fake_audio_source import FakeAudioSource
from app.infrastructure.config.settings import Settings

_FIXTURE_DIR = Path(__file__).parent / "fixtures"


def _find_fixture() -> Path | None:
    env = os.environ.get("STT_TEST_WAV")
    if env and Path(env).is_file():
        return Path(env)
    if _FIXTURE_DIR.is_dir():
        wavs = sorted(_FIXTURE_DIR.glob("*.wav"))
        if wavs:
            return wavs[0]
    return None


def _require_engine_and_fixture() -> Path:
    pytest.importorskip("faster_whisper", reason="faster-whisper engine not installed")
    fixture = _find_fixture()
    if fixture is None:
        pytest.skip(
            "No STT fixture. Set STT_TEST_WAV=/path/to/english.wav or drop a .wav in "
            "tests/fixtures/ to run the real-model STT test."
        )
    return fixture


async def test_real_model_transcribes_english_wav_to_ordered_segments():
    fixture = _require_engine_and_fixture()
    from app.infrastructure.stt.faster_whisper_speech_to_text import (
        FasterWhisperSpeechToText,
    )

    settings = Settings()  # base.en / cpu / int8 by default
    source = FakeAudioSource(settings, str(fixture))
    stt = FasterWhisperSpeechToText(settings)

    segments = [seg async for seg in stt.transcribe(source.frames())]

    assert segments, "expected at least one transcript segment"
    assert any(s.is_final for s in segments), "expected a finalized segment"
    assert any(s.text.strip() for s in segments), "expected non-empty transcript text"

    starts = [s.start_ms for s in segments]
    assert starts == sorted(starts)  # ordered by stream time
    for seg in segments:
        assert 0 <= seg.start_ms <= seg.end_ms
