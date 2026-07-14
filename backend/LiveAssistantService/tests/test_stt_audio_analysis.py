from __future__ import annotations

import numpy as np

from app.infrastructure.stt.audio_analysis import (
    DEFAULT_SILENCE_RMS,
    is_silent,
    pcm16_to_float32,
)


def test_pcm16_to_float32_scales_to_unit_range():
    pcm = np.array([0, 32767, -32768, 16384], dtype="<i2").tobytes()

    out = pcm16_to_float32(pcm)

    assert out.dtype == np.float32
    assert out[0] == 0.0
    assert out[1] == np.float32(32767 / 32768.0)
    assert out[2] == -1.0
    assert np.isclose(out[3], 0.5)


def test_pcm16_to_float32_empty():
    assert pcm16_to_float32(b"").shape == (0,)


def test_is_silent_true_for_zeros_false_for_tone():
    quiet = np.zeros(1600, dtype=np.float32)
    t = np.arange(1600)
    loud = (0.5 * np.sin(2 * np.pi * 300 * t / 16000)).astype(np.float32)

    assert is_silent(quiet, DEFAULT_SILENCE_RMS) is True
    assert is_silent(loud, DEFAULT_SILENCE_RMS) is False


def test_is_silent_respects_threshold():
    faint = np.full(1600, 0.002, dtype=np.float32)  # RMS 0.002

    assert is_silent(faint, threshold=0.008) is True
    assert is_silent(faint, threshold=0.001) is False
