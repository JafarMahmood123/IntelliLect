"""Offline ``AudioSource`` backed by a local WAV file (or a synthesized tone).

This is what makes every LATER phase buildable and testable with NO LiveKit and NO
live session: point it at a WAV (any sample rate / channel count — it is normalized
through the same path as the real source) and it emits teacher ``AudioFrame``s.
"""

from __future__ import annotations

import math
import wave
from collections.abc import AsyncIterator

from app.application.ports.audio_source import AudioSource
from app.domain.audio.audio_frame import AudioFrame
from app.domain.entities.session_context import SessionContext
from app.infrastructure.audio.normalization import normalize_pcm
from app.infrastructure.config.settings import Settings

_BYTES_PER_SAMPLE = 2  # int16


class FakeAudioSource(AudioSource):
    """Yields normalized frames from a WAV file, or a sine tone if no path is given.

    ``connect``/``disconnect`` are no-ops. Frames come out mono at
    ``settings.target_sample_rate``, sliced into ``frame_duration_ms`` chunks with a
    stream-time ``timestamp`` — identical in shape to what ``LiveKitAudioSource``
    produces, so downstream code cannot tell them apart.
    """

    def __init__(
        self,
        settings: Settings,
        wav_path: str | None = None,
        *,
        frame_duration_ms: int = 20,
        tone_seconds: float = 1.0,
        tone_hz: float = 440.0,
    ) -> None:
        self._settings = settings
        self._wav_path = wav_path
        self._frame_duration_ms = frame_duration_ms
        self._tone_seconds = tone_seconds
        self._tone_hz = tone_hz

    async def connect(self, session: SessionContext) -> None:  # noqa: D102 - no-op
        return None

    async def disconnect(self) -> None:  # noqa: D102 - no-op
        return None

    async def frames(self) -> AsyncIterator[AudioFrame]:
        target_rate = self._settings.target_sample_rate
        target_channels = self._settings.target_channels

        pcm, src_rate, src_channels = self._read_source()
        normalized = normalize_pcm(
            pcm, src_rate, src_channels, target_rate, target_channels
        )

        frame_samples = max(1, target_rate * self._frame_duration_ms // 1000)
        frame_bytes = frame_samples * _BYTES_PER_SAMPLE * target_channels

        elapsed_samples = 0
        for start in range(0, len(normalized), frame_bytes):
            chunk = normalized[start : start + frame_bytes]
            yield AudioFrame(
                pcm=chunk,
                sample_rate=target_rate,
                channels=target_channels,
                timestamp=elapsed_samples / target_rate,
            )
            elapsed_samples += len(chunk) // (_BYTES_PER_SAMPLE * target_channels)

    def _read_source(self) -> tuple[bytes, int, int]:
        """Return (pcm, sample_rate, channels) from the WAV file or a synthesized tone."""
        if self._wav_path is None:
            return self._synth_tone(), self._settings.target_sample_rate, 1

        with wave.open(self._wav_path, "rb") as wav:
            if wav.getsampwidth() != _BYTES_PER_SAMPLE:
                raise ValueError(
                    f"Only 16-bit PCM WAV is supported, got "
                    f"{wav.getsampwidth() * 8}-bit: {self._wav_path}"
                )
            channels = wav.getnchannels()
            rate = wav.getframerate()
            pcm = wav.readframes(wav.getnframes())
        return pcm, rate, channels

    def _synth_tone(self) -> bytes:
        """A mono int16 sine wave at the target rate — a deterministic default signal."""
        rate = self._settings.target_sample_rate
        n = int(rate * self._tone_seconds)
        amplitude = 16000  # comfortably within int16 range
        buf = bytearray(n * _BYTES_PER_SAMPLE)
        for i in range(n):
            value = int(amplitude * math.sin(2.0 * math.pi * self._tone_hz * i / rate))
            # int16 little-endian
            buf[2 * i] = value & 0xFF
            buf[2 * i + 1] = (value >> 8) & 0xFF
        return bytes(buf)
