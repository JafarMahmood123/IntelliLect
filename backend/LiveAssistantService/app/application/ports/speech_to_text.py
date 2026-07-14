from __future__ import annotations

from abc import ABC, abstractmethod
from collections.abc import AsyncIterator

from app.domain.audio.audio_frame import AudioFrame


class SpeechToText(ABC):
    """Port for turning a stream of teacher audio into incremental transcript text.

    STUB — NOT IMPLEMENTED THIS PHASE (later phase: streaming STT). The method
    signature is fixed here so the future live-loop orchestrator can be written and
    tested against the abstraction; every method raises ``NotImplementedError``.
    """

    @abstractmethod
    def transcribe(self, frames: AsyncIterator[AudioFrame]) -> AsyncIterator[str]:
        """Consume normalized audio frames and yield incremental transcript text.

        Expected later-phase behavior: emit partial/final text segments as speech is
        recognized, so a downstream boundary detector can decide when the teacher has
        finished an "idea". Returns an async iterator of text segments.
        """
        raise NotImplementedError
