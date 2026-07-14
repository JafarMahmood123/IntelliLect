from __future__ import annotations

from abc import ABC, abstractmethod

from app.domain.entities.session_context import SessionContext
from app.domain.evaluation.teacher_suggestion import TeacherSuggestion


class FeedbackSink(ABC):
    """Port for privately delivering a suggestion to the teacher — and no one else.

    Implemented by ``LiveKitFeedbackSink``, which publishes a reliable LiveKit data
    message targeted to ``session.teacher_identity`` ONLY. The teacher-only invariant
    is the whole point of this port: an implementation must never broadcast to the
    room, so a student can never receive feedback.

    This phase (LA-5) only delivers; it does NOT rate-limit / dedup / suppress (LA-7)
    or wire the live session (LA-6).
    """

    @abstractmethod
    async def send(
        self, session: SessionContext, suggestion: TeacherSuggestion
    ) -> None:
        """Deliver ``suggestion`` privately to the teacher of ``session``.

        Implementations target ``session.teacher_identity`` exclusively and raise a
        clear, catchable error if delivery is impossible (no room / teacher absent),
        so the caller can log and continue — a failed send must never break the loop.
        """
        raise NotImplementedError
