from abc import ABC, abstractmethod
from datetime import datetime
from uuid import UUID

from app.domain.entities.document import Document
from app.domain.enums.document_status import DocumentStatus


class DocumentRepository(ABC):
    """Persistence port for Document aggregate."""

    @abstractmethod
    async def add(self, document: Document) -> Document:
        """Insert a document. Should be idempotent on file_id (upsert)."""
        raise NotImplementedError

    @abstractmethod
    async def get_by_file_id(self, file_id: UUID) -> Document | None:
        raise NotImplementedError

    @abstractmethod
    async def update_status(
        self, file_id: UUID, status: DocumentStatus, last_error: str | None = None
    ) -> None:
        raise NotImplementedError

    @abstractmethod
    async def delete_by_file_id(self, file_id: UUID) -> bool:
        """Delete the document. Returns True if a row was removed."""
        raise NotImplementedError

    @abstractmethod
    async def claim_for_processing(
        self, document: Document, now: datetime
    ) -> Document | None:
        """Atomically claim a document for processing (concurrency-safe).

        Transitions a claimable (Pending) row -> Processing, sets
        processing_started_at = now and increments attempts, creating the row from
        `document` if it does not exist yet. Returns the claimed Document, or None if
        another worker already owns it (Processing) or it is in a terminal state
        (Done/Failed) — the caller must then exit without reprocessing.
        """
        raise NotImplementedError

    @abstractmethod
    async def find_stale_processing(self, threshold: datetime) -> list[Document]:
        """Documents stuck in Processing with processing_started_at < threshold."""
        raise NotImplementedError

    @abstractmethod
    async def reset_to_pending(
        self, file_id: UUID, *, reset_attempts: bool
    ) -> Document | None:
        """Reset a document to Pending and clear last_error / processing_started_at.

        Used by stale recovery (reset_attempts=False, keep the attempt count) and by
        the manual reindex endpoint (reset_attempts=True, start fresh). Returns the
        updated Document, or None if the file_id is unknown.
        """
        raise NotImplementedError
