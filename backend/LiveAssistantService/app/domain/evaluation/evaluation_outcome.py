from __future__ import annotations

from dataclasses import dataclass

from app.domain.evaluation.teacher_suggestion import TeacherSuggestion


@dataclass(frozen=True)
class EvaluationOutcome:
    """The result of evaluating one CompletedIdea against the course material.

    Pure domain object. ``has_feedback`` is False for the common case (no clear
    problem, or nothing relevant retrieved); ``suggestion`` is populated only when
    ``has_feedback`` is True.
    """

    has_feedback: bool
    suggestion: TeacherSuggestion | None = None

    @classmethod
    def none(cls) -> "EvaluationOutcome":
        """The silence-biased default: no feedback."""
        return cls(has_feedback=False, suggestion=None)
