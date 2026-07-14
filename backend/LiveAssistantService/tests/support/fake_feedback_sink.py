"""A recording ``FeedbackSink`` for testing LA-5 without a live room.

Records each (session, suggestion) call and exposes the resolved target identity
(``session.teacher_identity``) so tests can assert WHAT would be sent and TO WHOM —
and that a student identity is never the target.
"""

from __future__ import annotations

from app.application.ports.feedback_sink import FeedbackSink
from app.domain.entities.session_context import SessionContext
from app.domain.evaluation.teacher_suggestion import TeacherSuggestion


class FakeFeedbackSink(FeedbackSink):
    def __init__(self, *, error: Exception | None = None) -> None:
        self._error = error
        self.calls: list[tuple[SessionContext, TeacherSuggestion]] = []

    @property
    def called(self) -> bool:
        return bool(self.calls)

    @property
    def target_identities(self) -> list[str]:
        """The identity each recorded suggestion was addressed to (teacher only)."""
        return [session.teacher_identity for session, _ in self.calls]

    async def send(
        self, session: SessionContext, suggestion: TeacherSuggestion
    ) -> None:
        self.calls.append((session, suggestion))
        if self._error is not None:
            raise self._error
