from __future__ import annotations

from typing import Annotated

from fastapi import Depends

from app.application.ports.audio_source import AudioSource
from app.application.ports.speech_to_text import SpeechToText
from app.infrastructure.audio.fake_audio_source import FakeAudioSource
from app.infrastructure.audio.livekit_audio_source import LiveKitAudioSource
from app.infrastructure.config.settings import Settings, get_settings
from app.infrastructure.stt.faster_whisper_speech_to_text import (
    FasterWhisperSpeechToText,
)

# --- Composition root ---------------------------------------------------------
# The only place API code names concrete infrastructure classes. Everything
# downstream depends on the port abstractions in app.application.ports.
#
# No endpoint constructs an AudioSource yet — session lifecycle wiring is LA-6.
# The factories below are the seam the future live-loop orchestrator/session
# endpoint will call, and are exercised today by scripts/capture_check.py.

SettingsDep = Annotated[Settings, Depends(get_settings)]


def build_live_audio_source(settings: Settings) -> AudioSource:
    """The real server-side agent that captures a teacher's audio from a LiveKit room."""
    return LiveKitAudioSource(settings)


def build_fake_audio_source(
    settings: Settings, wav_path: str | None = None
) -> AudioSource:
    """Offline AudioSource (WAV/tone) — no LiveKit. Used by tests and the CLI default."""
    return FakeAudioSource(settings, wav_path)


def build_speech_to_text(settings: Settings) -> SpeechToText:
    """The streaming English STT engine (faster-whisper / CTranslate2).

    Not wired into a live session yet (that is LA-6); registered here so it is
    injectable and replaceable behind the ``SpeechToText`` port. The faster-whisper
    model is loaded lazily on first use (or via ``warmup()``), so constructing this
    is cheap and does not require the engine to be installed at import time.
    """
    return FasterWhisperSpeechToText(settings)
