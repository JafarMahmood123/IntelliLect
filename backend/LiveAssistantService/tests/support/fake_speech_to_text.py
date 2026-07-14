"""A deterministic ``SpeechToText`` for building/testing LATER phases (LA-3+) with
no real model, no faster-whisper, and no audio.

LA-3 (idea/boundary detection) consumes ``TranscriptSegment``s; it can depend on
this fake to get a scripted, repeatable stream instead of the stochastic Whisper
output. It lives under ``tests/support`` (not ``app``) because it is test scaffolding.
"""

from __future__ import annotations

from collections.abc import AsyncIterator

from app.application.ports.speech_to_text import SpeechToText
from app.domain.audio.audio_frame import AudioFrame
from app.domain.transcript.transcript_segment import TranscriptSegment


class FakeSpeechToText(SpeechToText):
    """Yields a fixed list of ``TranscriptSegment``s, ignoring the audio content.

    The incoming ``frames`` iterator is drained (so the upstream ``AudioSource``
    completes) but its samples are not inspected — output is fully scripted.
    """

    def __init__(self, segments: list[TranscriptSegment]) -> None:
        self._segments = list(segments)

    async def transcribe(
        self, frames: AsyncIterator[AudioFrame]
    ) -> AsyncIterator[TranscriptSegment]:
        async for _frame in frames:
            pass  # consume the stream to completion; content is irrelevant
        for segment in self._segments:
            yield segment

    @classmethod
    def from_final_texts(
        cls, texts: list[str], *, segment_ms: int = 1000, pause_after_each: bool = True
    ) -> "FakeSpeechToText":
        """Convenience: one final, pause-terminated segment per text, back to back."""
        segments = [
            TranscriptSegment(
                text=text,
                start_ms=i * segment_ms,
                end_ms=(i + 1) * segment_ms,
                is_final=True,
                followed_by_pause=pause_after_each,
            )
            for i, text in enumerate(texts)
        ]
        return cls(segments)
