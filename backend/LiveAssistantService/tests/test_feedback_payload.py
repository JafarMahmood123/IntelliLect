"""The feedback wire contract (pure serialization) — no LiveKit."""

from __future__ import annotations

from datetime import datetime, timezone
from uuid import uuid4

from app.domain.entities.session_context import SessionContext
from app.domain.evaluation.feedback_type import FeedbackType
from app.domain.evaluation.retrieved_chunk import RetrievedChunk
from app.domain.evaluation.teacher_suggestion import TeacherSuggestion
from app.infrastructure.feedback.feedback_payload import build_feedback_payload

_WHEN = datetime(2026, 7, 14, 9, 30, 0, tzinfo=timezone.utc)


def _session(session_id, teacher="teacher-1") -> SessionContext:
    return SessionContext(session_id, uuid4(), teacher, "room")


def test_payload_matches_schema_and_maps_citations_to_sources():
    session_id, doc1, doc2 = uuid4(), uuid4(), uuid4()
    suggestion = TeacherSuggestion(
        text="Reconsider the location [1]; clarify [2].",
        type=FeedbackType.DISCREPANCY,
        citations=[1, 2],
        sources=[
            RetrievedChunk("raw text A", 0.8, uuid4(), doc1, slide=4),
            RetrievedChunk("raw text B", 0.6, uuid4(), doc2, page=12, section="Intro"),
        ],
    )

    payload = build_feedback_payload(_session(session_id), suggestion, version=1, created_at=_WHEN)

    assert payload == {
        "type": "teaching_suggestion",
        "version": 1,
        "session_id": str(session_id),
        "feedback_type": "discrepancy",
        "text": "Reconsider the location [1]; clarify [2].",
        "sources": [
            {"citation": 1, "document_id": str(doc1), "page": None, "slide": 4, "section": None},
            {"citation": 2, "document_id": str(doc2), "page": 12, "slide": None, "section": "Intro"},
        ],
        "created_at": "2026-07-14T09:30:00+00:00",
    }


def test_sources_omit_raw_chunk_text():
    suggestion = TeacherSuggestion(
        "see [1]", FeedbackType.GAP, [1], [RetrievedChunk("SECRET RAW TEXT", 0.9, uuid4(), uuid4(), page=1)]
    )

    payload = build_feedback_payload(_session(uuid4()), suggestion, version=1, created_at=_WHEN)

    assert "text" not in payload["sources"][0]  # only citation + locations, no raw text
    assert "SECRET RAW TEXT" not in str(payload["sources"])


def test_feedback_type_is_lowercased_for_each_kind():
    for kind, expected in [
        (FeedbackType.DISCREPANCY, "discrepancy"),
        (FeedbackType.GAP, "gap"),
        (FeedbackType.UNCLEAR, "unclear"),
    ]:
        suggestion = TeacherSuggestion("x", kind, [], [])
        payload = build_feedback_payload(_session(uuid4()), suggestion, version=1, created_at=_WHEN)
        assert payload["feedback_type"] == expected


def test_version_is_stamped_from_argument():
    suggestion = TeacherSuggestion("x", FeedbackType.UNCLEAR, [], [])
    payload = build_feedback_payload(_session(uuid4()), suggestion, version=7, created_at=_WHEN)
    assert payload["version"] == 7
