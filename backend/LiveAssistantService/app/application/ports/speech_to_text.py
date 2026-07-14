from __future__ import annotations

from abc import ABC, abstractmethod
from collections.abc import AsyncIterator

from app.domain.audio.audio_frame import AudioFrame
from app.domain.transcript.transcript_segment import TranscriptSegment


class SpeechToText(ABC):
    """Port for turning a stream of teacher audio into transcript segments.

    Implemented in the infrastructure layer (``FasterWhisperSpeechToText``); a
    deterministic ``FakeSpeechToText`` (test support) lets later phases be built and
    tested without the real model. The application/domain layers depend only on this
    abstraction, so the STT engine is swappable and its resource cost tunable.

    Input frames are the normalized ``TARGET_SAMPLE_RATE``/mono stream from an
    ``AudioSource`` — the port neither knows nor cares whether they came from LiveKit
    or ``FakeAudioSource``.
    """

    @abstractmethod
    def transcribe(
        self, frames: AsyncIterator[AudioFrame]
    ) -> AsyncIterator[TranscriptSegment]:
        """Consume normalized audio frames and yield ordered ``TranscriptSegment``s.

        Emits interim (``is_final=False``) segments as text stabilizes and a final
        (``is_final=True``) segment per utterance, flagging ``followed_by_pause`` when
        a silence gap closed it. Implementations return an async generator, so this
        method itself is not ``async`` — callers use ``async for seg in stt.transcribe(...)``.
        """
        raise NotImplementedError
