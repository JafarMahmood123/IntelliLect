"""FeedbackSink that delivers a suggestion privately to the teacher over the agent's
existing LiveKit connection.

It serializes the suggestion to the versioned payload contract and publishes it as a
reliable data message targeted to ``session.teacher_identity`` ONLY, via an
``AgentDataChannel`` (implemented by the LA-1 agent — the single room connection). It
holds no LiveKit types itself; the teacher-only guarantee is structural — the channel
has no broadcast method and only the teacher identity is ever passed.
"""

from __future__ import annotations

import json
import logging
from collections.abc import Callable
from datetime import datetime, timezone

from app.application.ports.agent_data_channel import AgentDataChannel
from app.application.ports.feedback_sink import FeedbackSink
from app.domain.entities.session_context import SessionContext
from app.domain.evaluation.teacher_suggestion import TeacherSuggestion
from app.infrastructure.feedback.feedback_payload import (
    MESSAGE_TYPE,
    build_feedback_payload,
)

logger = logging.getLogger("liveassistant.feedback")

# LiveKit data-message topic, so the teacher's frontend can filter for suggestions.
FEEDBACK_TOPIC = MESSAGE_TYPE


class FeedbackDeliveryError(RuntimeError):
    """Raised when a suggestion could not be delivered to the teacher.

    Catchable by the caller (FeedbackDispatcher), which logs and continues — a failed
    send must never break the live loop.
    """


def _utcnow() -> datetime:
    return datetime.now(timezone.utc)


class LiveKitFeedbackSink(FeedbackSink):
    def __init__(
        self,
        channel: AgentDataChannel,
        *,
        message_version: int,
        clock: Callable[[], datetime] = _utcnow,
    ) -> None:
        self._channel = channel
        self._version = message_version
        self._clock = clock  # injectable for deterministic tests

    async def send(
        self, session: SessionContext, suggestion: TeacherSuggestion
    ) -> None:
        payload = build_feedback_payload(
            session, suggestion, version=self._version, created_at=self._clock()
        )
        data = json.dumps(payload).encode("utf-8")
        try:
            # Target the teacher identity ONLY — never the room, never a student.
            await self._channel.publish_to_identity(
                session.teacher_identity, data, topic=FEEDBACK_TOPIC
            )
        except Exception as exc:  # noqa: BLE001 — normalize to a catchable delivery error
            raise FeedbackDeliveryError(
                f"Could not deliver feedback to teacher '{session.teacher_identity}': {exc}"
            ) from exc
        logger.debug(
            "Delivered %s (%d bytes) to teacher %s",
            FEEDBACK_TOPIC, len(data), session.teacher_identity,
        )
