"""Audio format normalization: any PCM -> mono at the target sample rate.

Kept free of LiveKit imports on purpose. Both ``FakeAudioSource`` and
``LiveKitAudioSource`` route their raw PCM through here, and the offline tests
exercise this module directly — so normalization is verified without a live room.

All PCM is signed 16-bit little-endian. Downmix is a channel average; resampling is
linear interpolation, which is adequate for speech STT and has no external
dependency. A higher-quality polyphase resampler can replace ``_resample`` later
without changing the interface.
"""

from __future__ import annotations

import numpy as np

_DTYPE = np.dtype("<i2")  # signed 16-bit little-endian PCM
_INT16_MIN, _INT16_MAX = -32768, 32767


def _resample(mono: np.ndarray, src_rate: int, target_rate: int) -> np.ndarray:
    """Linear-interpolate a mono float signal from ``src_rate`` to ``target_rate``."""
    if src_rate == target_rate or mono.size == 0:
        return mono
    n_src = mono.shape[0]
    n_target = int(round(n_src * target_rate / src_rate))
    if n_target <= 0:
        return mono[:0]
    # Sample positions of the source grid mapped onto the (shorter/longer) target grid.
    src_positions = np.arange(n_src, dtype=np.float64)
    target_positions = np.linspace(0.0, n_src - 1, num=n_target, dtype=np.float64)
    return np.interp(target_positions, src_positions, mono)


def normalize_pcm(
    pcm: bytes,
    src_rate: int,
    src_channels: int,
    target_rate: int,
    target_channels: int = 1,
) -> bytes:
    """Convert interleaved int16 PCM to ``target_channels`` mono at ``target_rate``.

    Only mono output (``target_channels == 1``) is supported this phase — the whole
    downstream pipeline assumes mono. Returns int16 little-endian PCM bytes.
    """
    if target_channels != 1:
        raise ValueError("Only mono (target_channels=1) output is supported.")
    if src_channels < 1:
        raise ValueError("src_channels must be >= 1")
    if not pcm:
        return b""

    samples = np.frombuffer(pcm, dtype=_DTYPE)
    # Drop a trailing partial frame if the buffer isn't channel-aligned (defensive).
    usable = (samples.shape[0] // src_channels) * src_channels
    samples = samples[:usable].reshape(-1, src_channels)

    # Downmix to mono by averaging channels in float space to avoid int overflow.
    mono = samples.mean(axis=1).astype(np.float64)

    mono = _resample(mono, src_rate, target_rate)

    clipped = np.clip(np.round(mono), _INT16_MIN, _INT16_MAX).astype(_DTYPE)
    return clipped.tobytes()
