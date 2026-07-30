"""MassTransit wire conventions: envelope construction, parsing, and exchange naming.

This service talks to a .NET MassTransit bus without running MassTransit, so the conventions
have to be reproduced by hand. They were already reproduced once, inline, in the summary
publisher; now that there is also a CONSUMER and an outbox relay, they live here so the three
agree by construction rather than by copy-paste.

The conventions:
  * exchange = the namespace-qualified message type, e.g.
    ``IntelliLect.Contracts.Messages:SessionSummaryReadyMessage``, declared durable FANOUT
  * body = a JSON envelope whose ``message`` holds the payload and whose ``messageType`` lists
    URNs of the form ``urn:message:<type>``
  * content type = ``application/vnd.masstransit+json``

PARSING IS DELIBERATELY LENIENT. Envelopes written by real MassTransit carry keys this service
never writes — ``correlationId``, ``initiatorId``, ``host``, ``expirationTime`` — and .NET
serializes payload properties in PascalCase while this service writes camelCase. Both directions
must survive, so ``payload_field`` looks a property up case-insensitively and unknown envelope
keys are ignored rather than rejected.
"""

from __future__ import annotations

from datetime import datetime, timezone
from typing import Any
from uuid import UUID, uuid4

MASSTRANSIT_CONTENT_TYPE = "application/vnd.masstransit+json"

# Message types exchanged with ClassroomService.
SUMMARY_READY_TYPE = "IntelliLect.Contracts.Messages:SessionSummaryReadyMessage"
SUMMARY_REQUESTED_TYPE = "IntelliLect.Contracts.Messages:SessionSummaryRequestedMessage"

# Mirrors IntelliLect.Contracts.Messages.SummaryRequestReasons.
#
# These are NOT decorative. A manual request must REOPEN the run before claiming it: a Failed run
# is deliberately terminal — status Failed with next_attempt_at NULL is unclaimable, which is what
# stops the retry sweep from grinding on a hopeless summary forever. Without this distinction a
# teacher pressing Regenerate would lose the claim and the request would be silently deduped away,
# looking exactly like a no-op.
REASON_SESSION_ENDED = "SessionEnded"
REASON_MANUAL_TEACHER = "ManualTeacher"
REASON_MANUAL_SUPER_ADMIN = "ManualSuperAdmin"
MANUAL_REASONS = frozenset({REASON_MANUAL_TEACHER, REASON_MANUAL_SUPER_ADMIN})


def is_manual_request(reason: str) -> bool:
    """Whether this request came from a human and must therefore reopen a terminal run."""
    return reason in MANUAL_REASONS


def type_urn(message_type: str) -> str:
    return f"urn:message:{message_type}"


def build_envelope(
    message_type: str,
    payload: dict[str, Any],
    *,
    source_host: str,
    conversation_id: UUID | None = None,
    message_id: UUID | None = None,
    sent_time: datetime | None = None,
) -> dict[str, Any]:
    """Build a MassTransit-compatible envelope around ``payload``.

    ``sent_time`` is injectable so the outbox can record when the message was ENQUEUED rather
    than when the relay happened to drain it — otherwise a message delayed by a broker outage
    would claim to have been sent at the moment the outage ended.
    """
    return {
        "messageId": str(message_id or uuid4()),
        "conversationId": str(conversation_id) if conversation_id else None,
        "sourceAddress": f"rabbitmq://{source_host}/KnowledgeService",
        "destinationAddress": f"rabbitmq:///{message_type}",
        "messageType": [type_urn(message_type)],
        "message": payload,
        "sentTime": (sent_time or datetime.now(timezone.utc)).isoformat(),
        "headers": {},
    }


def envelope_has_type(envelope: dict[str, Any], message_type: str) -> bool:
    """Whether the envelope declares ``message_type`` among its types.

    MassTransit lists every type in the message's hierarchy, not just the concrete one, so this
    checks membership rather than equality.
    """
    declared = envelope.get("messageType") or []
    if isinstance(declared, str):  # tolerate a non-list producer
        declared = [declared]
    wanted = type_urn(message_type)
    return any(entry == wanted or entry == message_type for entry in declared)


def payload_field(payload: dict[str, Any], name: str) -> Any:
    """Read a payload property case-insensitively.

    .NET's serializer may emit PascalCase (``SessionId``) while this service writes camelCase
    (``sessionId``). Matching exactly would work in tests and fail against the real bus.
    """
    if name in payload:
        return payload[name]
    lowered = name.lower()
    for key, value in payload.items():
        if key.lower() == lowered:
            return value
    return None


def required_uuid(payload: dict[str, Any], name: str) -> UUID:
    """Read a required UUID property, raising a message that names the field."""
    raw = payload_field(payload, name)
    if raw is None or raw == "":
        raise ValueError(f"Message payload is missing required field '{name}'.")
    try:
        return UUID(str(raw))
    except (ValueError, AttributeError, TypeError) as exc:
        raise ValueError(f"Message field '{name}' is not a UUID: {raw!r}") from exc


def optional_uuid(payload: dict[str, Any], name: str) -> UUID | None:
    raw = payload_field(payload, name)
    if raw is None or raw == "":
        return None
    try:
        parsed = UUID(str(raw))
    except (ValueError, AttributeError, TypeError):
        return None
    # A nil UUID is how the .NET side spells "unknown" for a non-nullable Guid; treat it as absent
    # so it never reaches the S3 key template as a real classroom.
    return None if parsed.int == 0 else parsed
