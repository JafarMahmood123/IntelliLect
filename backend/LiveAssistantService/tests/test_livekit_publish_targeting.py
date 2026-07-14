"""LiveKitAudioSource.publish_to_identity — a reliable, single-target data publish.

Exercised with a fake room object (no LiveKit SDK): asserts the agent asks the room
to deliver reliably to exactly the one destination identity, and refuses to publish
when not connected.
"""

from __future__ import annotations

import pytest

from app.infrastructure.audio.livekit_audio_source import LiveKitAudioSource
from app.infrastructure.config.settings import Settings


class _FakeLocalParticipant:
    def __init__(self) -> None:
        self.calls: list[dict] = []

    async def publish_data(self, payload, *, reliable=False, destination_identities=None, topic=""):
        self.calls.append({
            "payload": payload,
            "reliable": reliable,
            "destination_identities": destination_identities,
            "topic": topic,
        })


class _FakeRoom:
    def __init__(self) -> None:
        self.local_participant = _FakeLocalParticipant()


async def test_publishes_reliably_to_single_identity():
    agent = LiveKitAudioSource(Settings())
    agent._room = _FakeRoom()  # simulate a connected room (no SDK)

    await agent.publish_to_identity("teacher-1", b"payload-bytes", topic="teaching_suggestion")

    (call,) = agent._room.local_participant.calls
    assert call["reliable"] is True
    assert call["destination_identities"] == ["teacher-1"]  # single target, never broadcast
    assert call["payload"] == b"payload-bytes"
    assert call["topic"] == "teaching_suggestion"


async def test_publish_without_connection_raises():
    agent = LiveKitAudioSource(Settings())  # never connected -> _room is None

    with pytest.raises(RuntimeError):
        await agent.publish_to_identity("teacher-1", b"x")
