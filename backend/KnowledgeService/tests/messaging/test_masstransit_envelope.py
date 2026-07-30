"""Cover the MassTransit wire format in both directions.

This is the seam where a mistake is invisible: a mis-shaped envelope or a case-sensitive field
lookup does not raise anywhere in this service — the message simply never arrives, or arrives and
is discarded, and the summary quietly never appears. So the tests assert against envelopes shaped
like the ones .NET actually emits, not just the ones this service writes.
"""

from __future__ import annotations

import json
from datetime import datetime, timezone
from uuid import UUID, uuid4

import pytest

from app.infrastructure.messaging.masstransit import (
    SUMMARY_REQUESTED_TYPE,
    build_envelope,
    envelope_has_type,
    is_manual_request,
    optional_uuid,
    payload_field,
    required_uuid,
    type_urn,
)
from app.infrastructure.messaging.summary_request_consumer import (
    SummaryRequestParseError,
    parse_summary_request,
)

SESSION_ID = UUID("11111111-1111-1111-1111-111111111111")
CLASSROOM_ID = UUID("22222222-2222-2222-2222-222222222222")


def _dotnet_envelope(**payload_overrides) -> bytes:
    """An envelope shaped like real MassTransit output.

    Deliberately includes keys this service never writes (correlationId, initiatorId, host,
    expirationTime) and PascalCase payload properties — the two ways a naive parser passes its
    own tests and then fails against the live bus.
    """
    payload = {
        "SessionId": str(SESSION_ID),
        "ClassroomId": str(CLASSROOM_ID),
        "RequestedByUserId": None,
        "Reason": "SessionEnded",
    }
    payload.update(payload_overrides)
    return json.dumps(
        {
            "messageId": str(uuid4()),
            "correlationId": str(uuid4()),
            "initiatorId": str(uuid4()),
            "conversationId": str(uuid4()),
            "sourceAddress": "rabbitmq://intellilect-mq/ClassroomService",
            "destinationAddress": f"rabbitmq:///{SUMMARY_REQUESTED_TYPE}",
            "messageType": [type_urn(SUMMARY_REQUESTED_TYPE)],
            "message": payload,
            "sentTime": "2026-07-30T12:00:00Z",
            "headers": {},
            "host": {"machineName": "abc", "processId": 1},
            "expirationTime": None,
        }
    ).encode("utf-8")


# --- parsing an incoming request -----------------------------------------------------


def test_parses_a_dotnet_shaped_envelope() -> None:
    session_id, classroom_id, reason = parse_summary_request(_dotnet_envelope())

    assert session_id == SESSION_ID
    assert classroom_id == CLASSROOM_ID
    assert reason == "SessionEnded"


def test_parses_camel_case_payload_too() -> None:
    """This service writes camelCase; both must work or one direction silently breaks."""
    body = json.dumps(
        {
            "messageType": [type_urn(SUMMARY_REQUESTED_TYPE)],
            "message": {
                "sessionId": str(SESSION_ID),
                "classroomId": str(CLASSROOM_ID),
                "reason": "ManualTeacher",
            },
        }
    ).encode("utf-8")

    session_id, classroom_id, reason = parse_summary_request(body)

    assert (session_id, classroom_id, reason) == (SESSION_ID, CLASSROOM_ID, "ManualTeacher")


def test_a_nil_classroom_guid_is_treated_as_unknown() -> None:
    """.NET spells "unknown" for a non-nullable Guid as all-zeros; it must not reach the S3 key."""
    body = _dotnet_envelope(ClassroomId="00000000-0000-0000-0000-000000000000")

    _, classroom_id, _ = parse_summary_request(body)

    assert classroom_id is None


def test_extra_envelope_keys_are_ignored() -> None:
    """Already covered implicitly, but pinned: unknown keys must never be a rejection reason."""
    parse_summary_request(_dotnet_envelope())  # contains host/initiatorId/expirationTime


def test_unexpected_message_type_still_parses() -> None:
    """A binding mix-up must not silently discard summaries over a naming detail."""
    body = json.dumps(
        {
            "messageType": ["urn:message:Some.Other:Message"],
            "message": {"sessionId": str(SESSION_ID)},
        }
    ).encode("utf-8")

    session_id, _, _ = parse_summary_request(body)

    assert session_id == SESSION_ID


@pytest.mark.parametrize(
    "body",
    [
        b"not json at all",
        json.dumps({"messageType": [], "message": None}).encode(),
        json.dumps({"message": {"classroomId": str(CLASSROOM_ID)}}).encode(),  # no sessionId
        json.dumps({"message": {"sessionId": "not-a-uuid"}}).encode(),
        json.dumps(["a", "list"]).encode(),
    ],
)
def test_unusable_messages_raise_a_parse_error(body: bytes) -> None:
    """These are acked and dropped, so the error type is what stops them poisoning the queue."""
    with pytest.raises(SummaryRequestParseError):
        parse_summary_request(body)


# --- building an outgoing envelope ---------------------------------------------------


def test_envelope_shape_matches_masstransit() -> None:
    envelope = build_envelope(
        SUMMARY_REQUESTED_TYPE,
        {"sessionId": str(SESSION_ID)},
        source_host="intellilect-mq",
        conversation_id=SESSION_ID,
        sent_time=datetime(2026, 7, 30, tzinfo=timezone.utc),
    )

    assert envelope["messageType"] == [f"urn:message:{SUMMARY_REQUESTED_TYPE}"]
    assert envelope["destinationAddress"] == f"rabbitmq:///{SUMMARY_REQUESTED_TYPE}"
    assert envelope["sourceAddress"] == "rabbitmq://intellilect-mq/KnowledgeService"
    assert envelope["message"] == {"sessionId": str(SESSION_ID)}
    assert envelope["sentTime"].startswith("2026-07-30")


def test_sent_time_is_injectable() -> None:
    """The outbox stamps ENQUEUE time, so a message delayed by an outage does not lie about it."""
    enqueued = datetime(2026, 7, 30, 9, 30, tzinfo=timezone.utc)

    envelope = build_envelope(
        SUMMARY_REQUESTED_TYPE, {}, source_host="h", sent_time=enqueued
    )

    assert envelope["sentTime"] == enqueued.isoformat()


def test_round_trip_through_our_own_envelope() -> None:
    envelope = build_envelope(
        SUMMARY_REQUESTED_TYPE,
        {"sessionId": str(SESSION_ID), "classroomId": str(CLASSROOM_ID), "reason": "SessionEnded"},
        source_host="intellilect-mq",
    )

    parsed = parse_summary_request(json.dumps(envelope).encode())

    assert parsed == (SESSION_ID, CLASSROOM_ID, "SessionEnded")


# --- helpers -------------------------------------------------------------------------


def test_envelope_has_type_accepts_urn_and_bare_and_lists() -> None:
    assert envelope_has_type({"messageType": [type_urn("A:B")]}, "A:B")
    assert envelope_has_type({"messageType": ["A:B"]}, "A:B")
    assert envelope_has_type({"messageType": type_urn("A:B")}, "A:B")  # non-list producer
    assert not envelope_has_type({"messageType": ["X:Y"]}, "A:B")
    assert not envelope_has_type({}, "A:B")


def test_payload_field_is_case_insensitive() -> None:
    assert payload_field({"SessionId": 1}, "sessionId") == 1
    assert payload_field({"sessionid": 1}, "SessionId") == 1
    assert payload_field({}, "missing") is None


def test_optional_uuid_tolerates_junk() -> None:
    assert optional_uuid({"a": ""}, "a") is None
    assert optional_uuid({"a": "nope"}, "a") is None
    assert optional_uuid({"a": str(SESSION_ID)}, "a") == SESSION_ID


def test_required_uuid_names_the_missing_field() -> None:
    with pytest.raises(ValueError, match="sessionId"):
        required_uuid({}, "sessionId")


def test_manual_reasons_are_recognised() -> None:
    """Getting this wrong makes Regenerate a silent no-op on a Failed summary."""
    assert is_manual_request("ManualTeacher")
    assert is_manual_request("ManualSuperAdmin")
    assert not is_manual_request("SessionEnded")
    assert not is_manual_request("Unknown")
