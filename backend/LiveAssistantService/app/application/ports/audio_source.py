from __future__ import annotations

from abc import ABC, abstractmethod
from collections.abc import AsyncIterator

from app.domain.audio.audio_frame import AudioFrame
from app.domain.entities.session_context import SessionContext


class AudioSource(ABC):
    """Port for a stream of the teacher's live audio.

    Implemented in the infrastructure layer (``LiveKitAudioSource`` against a real
    room, ``FakeAudioSource`` from a local WAV for offline development/tests). The
    application/domain layers depend only on this abstraction, so the future
    live-loop orchestrator neither knows nor cares whether frames come from LiveKit.

    Frames yielded by ``frames()`` are already normalized to the configured
    ``TARGET_SAMPLE_RATE`` / ``TARGET_CHANNELS`` (mono) and belong to the teacher
    only — a real implementation must never surface student audio.
    """

    @abstractmethod
    async def connect(self, session: SessionContext) -> None:
        """Join/attach to the session and begin capturing the teacher's audio.

        Must resolve which participant is the teacher (``session.teacher_identity``)
        and subscribe to that participant's audio only. Should tolerate the teacher
        not having joined yet (wait and subscribe on join).
        """
        raise NotImplementedError

    @abstractmethod
    def frames(self) -> AsyncIterator[AudioFrame]:
        """Yield normalized ``AudioFrame`` objects for the teacher until disconnect.

        The stream ends (the async iterator stops) when the source is disconnected
        or the underlying session ends. Implementations return an async generator, so
        this method itself is not ``async`` — callers use ``async for f in src.frames()``.
        """
        raise NotImplementedError

    @abstractmethod
    async def disconnect(self) -> None:
        """Leave the session and release all resources. Idempotent."""
        raise NotImplementedError
