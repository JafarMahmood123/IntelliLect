"""LiveKitFeedbackSink — teacher-only targeting, correct bytes, error resilience.

Uses FakeAgentDataChannel (records publishes); no LiveKit.
"""

from __future__ import annotations

import json
from datetime import datetime, timezone
from uuid import uuid4

import pytest

from app.domain.entities.session_context import SessionContext
from app.domain.evaluation.feedback_type import FeedbackType
from app.domain.evaluation.retrieved_chunk import RetrievedChunk
from app.domain.evaluation.teacher_suggestion import TeacherSuggestion
from app.infrastructure.feedback.livekit_feedback_sink import (
    FEEDBACK_TOPIC,
    FeedbackDeliveryError,
    LiveKitFeedbackSink,
)
from tests.support.fake_agent_data_channel import FakeAgentDataChannel

_WHEN = datetime(2026, 7, 14, 9, 30, 0, tzinfo=timezone.utc)


def _sink(channel, version=1) -> LiveKitFeedbackSink:
    return LiveKitFeedbackSink(channel, message_version=version, clock=lambda: _WHEN)


def _suggestion() -> TeacherSuggestion:
    return TeacherSuggestion(
        "Photosynthesis is in the chloroplast [1].",
        FeedbackType.DISCREPANCY,
        [1],
        [RetrievedChunk("raw", 0.8, uuid4(), uuid4(), slide=4)],
    )


async def test_publishes_to_teacher_identity_only_with_correct_payload():
    channel = FakeAgentDataChannel()
    session = SessionContext(uuid4(), uuid4(), teacher_identity="teacher-9", room_name="room")

    await _sink(channel).send(session, _suggestion())

    # Exactly one targeted publish — to the teacher, over the feedback topic.
    assert len(channel.publishes) == 1
    identity, payload, topic = channel.publishes[0]
    assert identity == "teacher-9"
    assert topic == FEEDBACK_TOPIC

    data = json.loads(payload)
    assert data["type"] == "teaching_suggestion"
    assert data["version"] == 1
    assert data["session_id"] == str(session.session_id)
    assert data["feedback_type"] == "discrepancy"
    assert data["sources"][0]["citation"] == 1 and data["sources"][0]["slide"] == 4
    assert data["created_at"] == "2026-07-14T09:30:00+00:00"


async def test_target_is_never_a_student_identity():
    channel = FakeAgentDataChannel()
    session = SessionContext(uuid4(), uuid4(), teacher_identity="teacher-1", room_name="room")

    await _sink(channel).send(session, _suggestion())

    (identity, _payload, _topic), = channel.publishes
    assert identity == session.teacher_identity
    assert identity != "student-7"  # a student is structurally never the destination


async def test_channel_error_becomes_feedback_delivery_error():
    channel = FakeAgentDataChannel(error=RuntimeError("room gone"))
    session = SessionContext(uuid4(), uuid4(), "teacher-1", "room")

    with pytest.raises(FeedbackDeliveryError):
        await _sink(channel).send(session, _suggestion())
