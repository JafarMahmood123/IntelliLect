from __future__ import annotations

from abc import ABC, abstractmethod

from app.domain.entities.session_context import SessionContext


class FeedbackSink(ABC):
    """Port for privately delivering a correction to the teacher — and no one else.

    STUB — NOT IMPLEMENTED THIS PHASE (later phase: feedback delivery). The concrete
    implementation will push the suggestion to the teacher alone (e.g. a LiveKit data
    message addressed only to ``session.teacher_identity``, or a side channel), so
    students never see it. Every method raises ``NotImplementedError``.
    """

    @abstractmethod
    async def send(self, session: SessionContext, suggestion: dict) -> None:
        """Deliver ``suggestion`` privately to the teacher of ``session``.

        Expected later-phase behavior: route the correction to the teacher's client
        only — never broadcast to the room. The ``suggestion`` shape comes from the
        ``BrainClient`` verdict and is finalized when that phase is built.
        """
        raise NotImplementedError
