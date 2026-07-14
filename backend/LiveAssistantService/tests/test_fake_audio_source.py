from __future__ import annotations

from pytest import approx

from app.domain.audio.audio_frame import AudioFrame
from app.infrastructure.audio.fake_audio_source import FakeAudioSource
from app.infrastructure.config.settings import Settings


def _settings() -> Settings:
    # All fields default; explicit here so the test is independent of the environment.
    return Settings(target_sample_rate=16000, target_channels=1)


async def _collect(source: FakeAudioSource) -> list[AudioFrame]:
    await source.connect(None)  # type: ignore[arg-type]  # connect is a no-op
    frames = [frame async for frame in source.frames()]
    await source.disconnect()
    return frames


async def test_fake_source_yields_mono_16k_frames_from_wav(mono_16k_wav):
    settings = _settings()
    source = FakeAudioSource(settings, str(mono_16k_wav), frame_duration_ms=20)

    frames = await _collect(source)

    assert frames, "expected at least one frame"
    for frame in frames:
        assert frame.sample_rate == 16000
        assert frame.channels == 1
        assert len(frame.pcm) % 2 == 0  # whole int16 samples
        assert frame.num_samples > 0

    # ~0.5s of audio at 16kHz -> ~8000 samples total.
    total_samples = sum(f.num_samples for f in frames)
    assert abs(total_samples - 8000) <= 20


async def test_fake_source_timestamps_are_monotonic_and_ordered(mono_16k_wav):
    source = FakeAudioSource(_settings(), str(mono_16k_wav), frame_duration_ms=20)

    frames = await _collect(source)

    timestamps = [f.timestamp for f in frames]
    assert timestamps[0] == 0.0
    assert timestamps == sorted(timestamps)
    # Each 20ms frame advances the stream clock by ~0.02s (last frame may be shorter).
    for earlier, later in zip(frames, frames[1:]):
        expected = earlier.timestamp + earlier.duration_seconds
        assert later.timestamp == approx(expected)


async def test_fake_source_synthesized_tone_is_normalized():
    source = FakeAudioSource(_settings(), wav_path=None, tone_seconds=0.25)

    frames = await _collect(source)

    assert frames
    assert all(f.sample_rate == 16000 and f.channels == 1 for f in frames)
    total_samples = sum(f.num_samples for f in frames)
    assert abs(total_samples - 4000) <= 20  # 0.25s * 16kHz
