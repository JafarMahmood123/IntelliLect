"""FakeSpeechToText (test support) must yield scripted segments deterministically so
LATER phases (LA-3+) can unit-test against the SpeechToText port with no real model.
"""

from __future__ import annotations

from app.domain.transcript.transcript_segment import TranscriptSegment
from app.infrastructure.audio.fake_audio_source import FakeAudioSource
from app.infrastructure.config.settings import Settings
from tests.support.fake_speech_to_text import FakeSpeechToText


async def _empty_frames():
    return
    yield  # make this an async generator


async def test_fake_yields_exact_scripted_segments():
    scripted = [
        TranscriptSegment("hello there", 0, 900, is_final=False, followed_by_pause=False),
        TranscriptSegment("hello there world", 0, 1500, is_final=True, followed_by_pause=True),
        TranscriptSegment("next idea", 1600, 2400, is_final=True, followed_by_pause=False),
    ]
    stt = FakeSpeechToText(scripted)

    out = [seg async for seg in stt.transcribe(_empty_frames())]

    assert out == scripted  # frozen dataclasses compare by value; order preserved


async def test_fake_is_deterministic_across_runs():
    stt = FakeSpeechToText.from_final_texts(["one", "two", "three"])

    first = [s async for s in stt.transcribe(_empty_frames())]
    second = [s async for s in stt.transcribe(_empty_frames())]

    assert first == second
    assert [s.text for s in first] == ["one", "two", "three"]
    assert all(s.is_final and s.followed_by_pause for s in first)


async def test_fake_drains_the_audio_source():
    # A real AudioSource stream must be consumed to completion even though the fake
    # ignores its content — proves an LA-3 consumer can drive source -> stt uniformly.
    settings = Settings(target_sample_rate=16000)
    source = FakeAudioSource(settings, wav_path=None, tone_seconds=0.1)
    stt = FakeSpeechToText.from_final_texts(["idea"])

    out = [seg async for seg in stt.transcribe(source.frames())]

    assert [s.text for s in out] == ["idea"]
