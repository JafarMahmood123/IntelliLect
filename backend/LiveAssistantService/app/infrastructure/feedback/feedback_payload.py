"""The versioned wire contract for a teaching suggestion, consumed by the teacher's
frontend.

Pure serialization — NO LiveKit import — so the contract is unit-testable on its own
and reusable across transports (LiveKit today, SignalR later). Raw chunk text is
deliberately omitted: citations + document locations are enough for the UI to
reference the source material.

Message shape (``version`` = FEEDBACK_MESSAGE_VERSION):
    {
      "type": "teaching_suggestion",
      "version": 1,
      "session_id": "...",
      "feedback_type": "discrepancy|gap|unclear",
      "text": "<the suggestion>",
      "sources": [ {"citation": 1, "document_id": "...", "page": n|null,
                    "slide": n|null, "section": "...|null"} ],
      "created_at": "<iso8601>"
    }
"""

from __future__ import annotations

from datetime import datetime
from typing import Any

from app.domain.entities.session_context import SessionContext
from app.domain.evaluation.teacher_suggestion import TeacherSuggestion

MESSAGE_TYPE = "teaching_suggestion"


def build_feedback_payload(
    session: SessionContext,
    suggestion: TeacherSuggestion,
    *,
    version: int,
    created_at: datetime,
) -> dict[str, Any]:
    """Serialize a ``TeacherSuggestion`` into the versioned frontend contract."""
    sources = [
        {
            "citation": citation,
            "document_id": str(chunk.document_id),
            "page": chunk.page,
            "slide": chunk.slide,
            "section": chunk.section,
        }
        for citation, chunk in zip(suggestion.citations, suggestion.sources)
    ]
    return {
        "type": MESSAGE_TYPE,
        "version": version,
        "session_id": str(session.session_id),
        "feedback_type": suggestion.type.value.lower(),
        "text": suggestion.text,
        "sources": sources,
        "created_at": created_at.isoformat(),
    }
