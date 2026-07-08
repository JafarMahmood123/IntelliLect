from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
from uuid import UUID, uuid4

from app.domain.enums.document_status import DocumentStatus


def _utcnow() -> datetime:
    return datetime.now(timezone.utc)


@dataclass
class Document:
    """A file ingested into the knowledge base.

    Pure domain object: no ORM, no framework imports. The persistence layer maps
    this to/from its SQLAlchemy model.
    """

    classroom_id: UUID
    file_id: UUID
    s3_key: str
    file_name: str
    content_type: str
    content_hash: str | None = None
    status: DocumentStatus = DocumentStatus.PENDING
    error: str | None = None
    id: UUID = field(default_factory=uuid4)
    created_at_utc: datetime = field(default_factory=_utcnow)
    updated_at_utc: datetime = field(default_factory=_utcnow)

    def mark(self, status: DocumentStatus, error: str | None = None) -> None:
        self.status = status
        self.error = error
        self.updated_at_utc = _utcnow()
