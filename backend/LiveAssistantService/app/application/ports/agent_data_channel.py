from __future__ import annotations

from abc import ABC, abstractmethod


class AgentDataChannel(ABC):
    """Port for sending a data message to ONE participant over the agent's connection.

    The server-side agent (LA-1 ``LiveKitAudioSource``) owns the single room
    connection and implements this so feedback can be published without opening a
    second connection. The method targets exactly one identity — there is
    deliberately NO broadcast method, which is what makes teacher-only delivery
    structurally impossible to get wrong (a student identity is never passed by the
    ``FeedbackSink``, and the whole room is never an option).
    """

    @abstractmethod
    async def publish_to_identity(
        self, identity: str, payload: bytes, *, topic: str = ""
    ) -> None:
        """Reliably deliver ``payload`` to the participant ``identity`` only.

        Raises a catchable error if the connection is unavailable. Reliable delivery
        (ordered, retried) is part of the contract — feedback must not be dropped by
        the transport.
        """
        raise NotImplementedError
