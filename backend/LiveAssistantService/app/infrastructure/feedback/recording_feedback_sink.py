"""A ``FeedbackSink`` that records delivered suggestions in-process instead of
publishing them over LiveKit.

Used only when ``AGENT_AUDIO_SOURCE == "fake"`` (integration testing): with a fake
audio ingress there is no LiveKit room connection to carry a data message, so the
teacher-only suggestion is stored here and exposed read-only via
``GET /api/internal/sessions/{id}/feedback``. It builds the SAME versioned payload as
``LiveKitFeedbackSink`` (via ``build_feedback_payload``), so what a test observes is
byte-for-byte what the frontend would receive.
"""

from __future__ import annotations

import logging
from collections.abc import Callable
from datetime import datetime, timezone
from typing import Any
from uuid import UUID

from app.application.ports.feedback_sink import FeedbackSink
from app.domain.entities.session_context import SessionContext
from app.domain.evaluation.teacher_suggestion import TeacherSuggestion
from app.infrastructure.feedback.feedback_payload import build_feedback_payload

logger = logging.getLogger("liveassistant.feedback")


def _utcnow() -> datetime:
    return datetime.now(timezone.utc)


class FeedbackRecorder:
    """Process-wide store of delivered feedback payloads, keyed by session id.

    Built once at startup (like the transcript store) and shared by every fake-mode
    pipeline AND the feedback read endpoint, so the endpoint returns exactly what the
    pipelines delivered.
    """

    def __init__(self) -> None:
        self._by_session: dict[UUID, list[dict[str, Any]]] = {}

    def record(self, session_id: UUID, payload: dict[str, Any]) -> None:
        self._by_session.setdefault(session_id, []).append(payload)

    def get(self, session_id: UUID) -> list[dict[str, Any]]:
        return list(self._by_session.get(session_id, []))


class RecordingFeedbackSink(FeedbackSink):
    """Records the versioned suggestion payload for later read-back (no LiveKit)."""

    def __init__(
        self,
        recorder: FeedbackRecorder,
        *,
        message_version: int,
        clock: Callable[[], datetime] = _utcnow,
    ) -> None:
        self._recorder = recorder
        self._version = message_version
        self._clock = clock

    async def send(
        self, session: SessionContext, suggestion: TeacherSuggestion
    ) -> None:
        payload = build_feedback_payload(
            session, suggestion, version=self._version, created_at=self._clock()
        )
        self._recorder.record(session.session_id, payload)
        logger.info(
            "Recorded feedback for teacher %s (fake audio mode).",
            session.teacher_identity,
        )
