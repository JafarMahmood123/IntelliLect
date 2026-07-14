"""A recording ``AgentDataChannel`` for testing LiveKitFeedbackSink without LiveKit.

Records every (identity, payload, topic) publish so tests can assert the exact target
identity and bytes — or raise a configured error to exercise delivery resilience.
"""

from __future__ import annotations

from app.application.ports.agent_data_channel import AgentDataChannel


class FakeAgentDataChannel(AgentDataChannel):
    def __init__(self, *, error: Exception | None = None) -> None:
        self._error = error
        self.publishes: list[tuple[str, bytes, str]] = []  # (identity, payload, topic)

    async def publish_to_identity(
        self, identity: str, payload: bytes, *, topic: str = ""
    ) -> None:
        self.publishes.append((identity, payload, topic))
        if self._error is not None:
            raise self._error
