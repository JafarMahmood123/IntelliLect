from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any
from uuid import UUID


@dataclass(frozen=True)
class SessionSummaryReadyMessage:
    """Outcome of the S-3 summary pipeline, published for ClassroomService (S-4).

    Mirrors the .NET contract ``IntelliLect.Contracts.Messages.SessionSummaryReadyMessage``
    and its ``SessionRecordingReadyMessage`` sibling: ONE message conveys success or
    failure via ``succeeded``/``error``. On success ``md_s3_key``/``pdf_s3_key`` point at
    the uploaded artifacts; on failure they are empty and ``error`` explains why so S-4
    can mark the summary Failed.
    """

    session_id: UUID
    classroom_id: UUID
    md_s3_key: str
    pdf_s3_key: str
    generated_at: datetime
    succeeded: bool = True
    error: str | None = None

    @classmethod
    def success(
        cls,
        session_id: UUID,
        classroom_id: UUID,
        md_s3_key: str,
        pdf_s3_key: str,
        generated_at: datetime,
    ) -> "SessionSummaryReadyMessage":
        return cls(
            session_id=session_id,
            classroom_id=classroom_id,
            md_s3_key=md_s3_key,
            pdf_s3_key=pdf_s3_key,
            generated_at=generated_at,
            succeeded=True,
            error=None,
        )

    @classmethod
    def failure(
        cls, session_id: UUID, classroom_id: UUID | None, error: str
    ) -> "SessionSummaryReadyMessage":
        return cls(
            session_id=session_id,
            # A nil UUID stands in when the classroom is unknown (e.g. transcript fetch
            # failed before the classroom_id was resolved).
            classroom_id=classroom_id or UUID(int=0),
            md_s3_key="",
            pdf_s3_key="",
            generated_at=datetime.now(timezone.utc),
            succeeded=False,
            error=error,
        )

    def to_contract(self) -> dict[str, Any]:
        """camelCase payload matching the .NET record's property names.

        MassTransit's System.Text.Json deserialization is case-insensitive, so these
        map cleanly onto ``SessionSummaryReadyMessage`` in IntelliLect.Contracts.
        """
        return {
            "sessionId": str(self.session_id),
            "classroomId": str(self.classroom_id),
            "mdS3Key": self.md_s3_key,
            "pdfS3Key": self.pdf_s3_key,
            "generatedAt": self.generated_at.isoformat(),
            "succeeded": self.succeeded,
            "error": self.error,
        }
