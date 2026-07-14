"""Offline test fixtures. No LiveKit server, no models, no network.

Every Settings field has a safe default, so nothing must be set for the app to
import. Tests that need LiveKit *configured* set the env vars and clear the settings
cache themselves (see test_health).
"""

from __future__ import annotations

import wave
from pathlib import Path

import numpy as np
import pytest


def write_wav(path: Path, samples: np.ndarray, sample_rate: int, channels: int) -> Path:
    """Write an int16 WAV fixture. `samples` is shape (frames, channels) or (frames,)."""
    data = np.ascontiguousarray(samples.astype("<i2"))
    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(channels)
        wav.setsampwidth(2)  # 16-bit
        wav.setframerate(sample_rate)
        wav.writeframes(data.tobytes())
    return path


def sine(frequency: float, seconds: float, sample_rate: int, amplitude: int = 16000) -> np.ndarray:
    """A mono int16 sine wave, shape (frames,)."""
    t = np.arange(int(sample_rate * seconds))
    return (amplitude * np.sin(2 * np.pi * frequency * t / sample_rate)).astype("<i2")


@pytest.fixture
def mono_16k_wav(tmp_path) -> Path:
    """A 0.5s mono 16kHz WAV — already at the normalization target."""
    return write_wav(tmp_path / "mono16k.wav", sine(440.0, 0.5, 16000), 16000, 1)


@pytest.fixture
def stereo_48k_wav(tmp_path) -> Path:
    """A 0.5s stereo 48kHz WAV — must be downmixed + downsampled by normalization."""
    left = sine(440.0, 0.5, 48000)
    right = sine(660.0, 0.5, 48000)
    stereo = np.stack([left, right], axis=1)  # shape (frames, 2)
    return write_wav(tmp_path / "stereo48k.wav", stereo, 48000, 2)
