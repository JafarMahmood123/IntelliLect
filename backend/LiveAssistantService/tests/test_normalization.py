from __future__ import annotations

import numpy as np

from app.infrastructure.audio.fake_audio_source import FakeAudioSource
from app.infrastructure.audio.normalization import normalize_pcm
from app.infrastructure.config.settings import Settings

_BYTES_PER_SAMPLE = 2


def test_normalize_downmixes_stereo_and_downsamples():
    # 0.1s of interleaved stereo int16 at 48kHz -> mono at 16kHz.
    frames = int(48000 * 0.1)
    left = np.full(frames, 1000, dtype="<i2")
    right = np.full(frames, 3000, dtype="<i2")
    stereo = np.stack([left, right], axis=1).astype("<i2").tobytes()

    out = normalize_pcm(stereo, src_rate=48000, src_channels=2, target_rate=16000)

    mono = np.frombuffer(out, dtype="<i2")
    # 48k -> 16k is a 3x decimation: ~1600 mono samples for 0.1s.
    assert abs(mono.shape[0] - 1600) <= 2
    # Channel average of 1000 and 3000 is 2000.
    assert np.all(np.abs(mono.astype(int) - 2000) <= 1)


def test_normalize_passthrough_when_already_mono_and_target_rate():
    mono = np.arange(-500, 500, dtype="<i2").tobytes()

    out = normalize_pcm(mono, src_rate=16000, src_channels=1, target_rate=16000)

    assert out == mono


def test_normalize_empty_is_empty():
    assert normalize_pcm(b"", src_rate=48000, src_channels=2, target_rate=16000) == b""


def test_normalize_upsamples_to_target_rate():
    # 8kHz mono -> 16kHz mono roughly doubles the sample count.
    frames = 8000
    mono8k = np.zeros(frames, dtype="<i2").tobytes()

    out = normalize_pcm(mono8k, src_rate=8000, src_channels=1, target_rate=16000)

    assert abs(len(out) // _BYTES_PER_SAMPLE - 16000) <= 2


async def test_stereo_48k_wav_normalized_to_mono_16k_through_fake_source(stereo_48k_wav):
    settings = Settings(target_sample_rate=16000, target_channels=1)
    source = FakeAudioSource(settings, str(stereo_48k_wav), frame_duration_ms=20)

    await source.connect(None)  # type: ignore[arg-type]
    frames = [f async for f in source.frames()]
    await source.disconnect()

    assert frames
    assert all(f.sample_rate == 16000 for f in frames)
    assert all(f.channels == 1 for f in frames)
    total_samples = sum(f.num_samples for f in frames)
    assert abs(total_samples - 8000) <= 20  # 0.5s * 16kHz mono
