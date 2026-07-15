"""Tiny, dependency-light audio helpers for the STT engine.

Deliberately free of any faster-whisper import so the energy/silence logic (which
drives pause detection) can be unit-tested offline without the model. All PCM is
signed 16-bit little-endian mono, matching the normalized ``AudioFrame`` stream.
"""

from __future__ import annotations

import numpy as np

_INT16_FULL_SCALE = 32768.0

# Default RMS threshold (on the [-1, 1] float signal) below which a frame is treated
# as silence. A simple energy gate — adequate for marking pauses; a proper VAD is a
# later, tunable upgrade. Exposed as a constructor arg on the engine for tuning.
DEFAULT_SILENCE_RMS = 0.008


def pcm16_to_float32(pcm: bytes) -> np.ndarray:
    """Decode int16 little-endian PCM bytes into a mono float32 array in [-1, 1]."""
    if not pcm:
        return np.zeros(0, dtype=np.float32)
    # .astype copies out of the read-only frombuffer view before scaling.
    return np.frombuffer(pcm, dtype="<i2").astype(np.float32) / _INT16_FULL_SCALE


def rms(samples: np.ndarray) -> float:
    """Root-mean-square level of a float signal (0.0 for empty)."""
    if samples.size == 0:
        return 0.0
    return float(np.sqrt(np.mean(np.square(samples, dtype=np.float64))))


def is_silent(samples: np.ndarray, threshold: float = DEFAULT_SILENCE_RMS) -> bool:
    """Whether a frame's energy is below the silence threshold."""
    return rms(samples) < threshold
