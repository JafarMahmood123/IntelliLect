from __future__ import annotations

from abc import ABC, abstractmethod
from uuid import UUID

from app.application.dtos.summary_dtos import TranscriptDocument


class TranscriptClient(ABC):
    """Port for fetching a session's assembled transcript from LiveAssistantService.

    Implemented in the infrastructure layer (LiveAssistantTranscriptClient) over the
    S-0 internal endpoint; the application layer depends only on this abstraction and
    is tested with a FakeTranscriptClient.
    """

    @abstractmethod
    async def fetch(self, session_id: UUID) -> TranscriptDocument:
        """Return the assembled transcript for a session.

        Raises a subclass-specific error if the transcript cannot be retrieved (e.g.
        the session is unknown or LiveAssistantService is unreachable).
        """
        raise NotImplementedError
