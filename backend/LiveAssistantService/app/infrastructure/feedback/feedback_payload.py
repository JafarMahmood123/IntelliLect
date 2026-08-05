"""The versioned wire contract for a teaching suggestion, consumed by the teacher's
frontend.

Pure serialization — NO LiveKit import — so the contract is unit-testable on its own
and reusable across transports (LiveKit today, SignalR later). Raw chunk text is
deliberately omitted: citations + document locations are enough for the UI to
reference the source material.

Message shape (``version`` = FEEDBACK_MESSAGE_VERSION):
    {
      "type": "teaching_suggestion",
      "version": 2,
      "session_id": "...",
      "feedback_type": "discrepancy|gap|likely",
      "severity": "incorrect|likely|missing",
      "text": "<the suggestion>",
      "incorrect_text": "<verbatim quote of what was wrong>|null",
      "corrected_text": "<what it should be>|null",
      "sources": [ {"citation": 1, "document_id": "...", "page": n|null,
                    "slide": n|null, "section": "...|null"} ],
      "created_at": "<iso8601>"
    }

Version 2 added ``severity`` and the span pair, and renamed the ``unclear`` feedback type to
``likely``. That last one is why this is a version bump and not an additive change: a client
still reading v1 would receive a type it has no case for. Both sides ship together and each
ignores versions it does not know, so the bump costs nothing and the alternative — quietly
changing the meaning of a value under an unchanged version number — is how contracts rot.

``severity`` is derived here rather than by the client. See ``FeedbackSeverity`` for why the
frontend must not be the thing that decides which colour a diagnostic label deserves.
"""

from __future__ import annotations

from datetime import datetime
from typing import Any

from app.domain.entities.session_context import SessionContext
from app.domain.evaluation.feedback_severity import severity_of
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
        "severity": severity_of(suggestion.type).value.lower(),
        "text": suggestion.text,
        # Explicit nulls rather than omitted keys: the client reads one shape whether or not the
        # brain could quote a span, and "absent" never has to be told apart from "not applicable".
        "incorrect_text": suggestion.incorrect_text,
        "corrected_text": suggestion.corrected_text,
        "sources": sources,
        "created_at": created_at.isoformat(),
    }
