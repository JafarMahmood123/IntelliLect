"""Exercise the FasterWhisperSpeechToText streaming state machine OFFLINE.

No model is loaded: a subclass overrides ``_transcribe_window`` with a stub, so this
verifies buffering, interim/final emission, pause detection, and timing against real
audio framing (via FakeAudioSource) without faster-whisper installed.
"""

from __future__ import annotations

import numpy as np

from app.domain.transcript.transcript_segment import TranscriptSegment
from app.infrastructure.audio.fake_audio_source import FakeAudioSource
from app.infrastructure.config.settings import Settings
from app.infrastructure.stt.faster_whisper_speech_to_text import (
    FasterWhisperSpeechToText,
)


class _StubSTT(FasterWhisperSpeechToText):
    """Returns fixed non-empty text so segments emit; records call count."""

    def __init__(self, settings: Settings) -> None:
        super().__init__(settings)
        self.calls = 0

    async def _transcribe_window(self, samples: np.ndarray, sample_rate: int) -> str:
        self.calls += 1
        assert samples.dtype == np.float32  # windows reach the engine as float32
        return "speech"


def _settings() -> Settings:
    # Small chunk so a 1.5s utterance yields multiple interims; default 0.8s pause.
    return Settings(
        target_sample_rate=16000,
        stt_chunk_seconds=0.5,
        stt_pause_seconds=0.8,
    )


async def _collect(stt, source) -> list[TranscriptSegment]:
    return [seg async for seg in stt.transcribe(source.frames())]


async def test_two_utterances_split_on_pause(tone_gap_wav):
    settings = _settings()
    stt = _StubSTT(settings)
    source = FakeAudioSource(settings, str(tone_gap_wav), frame_duration_ms=20)

    segments = await _collect(stt, source)

    finals = [s for s in segments if s.is_final]
    interims = [s for s in segments if not s.is_final]

    # A tone/silence/tone clip -> two utterances, each finalized once.
    assert len(finals) == 2
    assert interims, "expected interim segments during the first utterance"
    # The last segment emitted is a final one.
    assert segments[-1].is_final

    # Exactly the first utterance is closed by a detected pause.
    paused = [s for s in segments if s.followed_by_pause]
    assert len(paused) == 1
    assert paused[0].is_final
    assert finals[0].followed_by_pause is True
    assert finals[1].followed_by_pause is False


async def test_segment_timings_are_sane_and_ordered(tone_gap_wav):
    settings = _settings()
    stt = _StubSTT(settings)
    source = FakeAudioSource(settings, str(tone_gap_wav), frame_duration_ms=20)

    segments = await _collect(stt, source)

    assert segments
    # start_ms is non-decreasing; every segment has start <= end within the clip.
    starts = [s.start_ms for s in segments]
    assert starts == sorted(starts)
    for seg in segments:
        assert 0 <= seg.start_ms <= seg.end_ms <= 3600  # clip is ~3.5s long

    finals = [s for s in segments if s.is_final]
    # First utterance starts at ~0; second starts after the ~2.5s gap.
    assert finals[0].start_ms < 100
    assert finals[1].start_ms > 2000


async def test_all_final_segments_emitted_and_text_present(tone_gap_wav):
    settings = _settings()
    stt = _StubSTT(settings)
    source = FakeAudioSource(settings, str(tone_gap_wav), frame_duration_ms=20)

    segments = await _collect(stt, source)

    assert stt.calls > 0
    assert all(s.text for s in segments)
    assert any(s.is_final for s in segments)


async def test_pure_silence_yields_no_segments(silence_wav):
    settings = _settings()
    stt = _StubSTT(settings)
    source = FakeAudioSource(settings, str(silence_wav), frame_duration_ms=20)

    segments = await _collect(stt, source)

    # Leading silence is dropped and no utterance ever opens.
    assert segments == []
    assert stt.calls == 0
