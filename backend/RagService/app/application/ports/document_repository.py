from abc import ABC, abstractmethod
from collections.abc import Sequence
from dataclasses import dataclass
from datetime import datetime
from uuid import UUID

from app.domain.entities.document import Document
from app.domain.enums.document_status import DocumentStatus


@dataclass(frozen=True)
class DocumentListItem:
    """A document plus its chunk count, for the super-admin knowledge-base list."""

    file_id: UUID
    classroom_id: UUID
    file_name: str
    content_type: str
    size_bytes: int
    status: DocumentStatus
    attempts: int
    chunk_count: int


@dataclass(frozen=True)
class KnowledgeStats:
    """Aggregate knowledge-base figures for a classroom or the whole platform."""

    document_count: int
    status_counts: dict[str, int]
    total_chunks: int
    failed_count: int
    storage_bytes: int


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
    async def delete_by_classroom_id(self, classroom_id: UUID) -> int:
        """Delete every document belonging to a classroom. Returns the number removed.

        Chunks cascade at the DB level, but callers delete them explicitly first so
        behaviour matches regardless of the backing store. Idempotent.
        """
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

    # --- Super-admin knowledge-base management (read side) ---

    @abstractmethod
    async def list_paged(
        self,
        *,
        page: int,
        page_size: int,
        status: DocumentStatus | None,
        classroom_id: UUID | None,
        search: str | None,
    ) -> tuple[list[DocumentListItem], int]:
        """A page of documents (newest first) with each one's chunk count, plus the total
        matching count. Optional filters: indexing status, classroom, and a filename search."""
        raise NotImplementedError

    @abstractmethod
    async def get_statuses(self, file_ids: Sequence[UUID]) -> list[DocumentListItem]:
        """Batch status/chunk-count lookup for a set of file ids (enrichment for a file list
        whose registry lives in another service). Unknown ids are simply absent from the result."""
        raise NotImplementedError

    @abstractmethod
    async def stats(self, classroom_id: UUID | None) -> KnowledgeStats:
        """Aggregate figures for a classroom (or the whole platform when None)."""
        raise NotImplementedError

    @abstractmethod
    async def list_file_ids_for_reindex(
        self, classroom_id: UUID, *, failed_only: bool
    ) -> list[UUID]:
        """File ids of a classroom's documents to re-index (all, or only Failed ones)."""
        raise NotImplementedError

    @abstractmethod
    async def count_active(self, classroom_id: UUID) -> int:
        """How many of a classroom's documents are Pending or Processing. A non-zero count
        means indexing work is already in flight (used to reject a piling-on bulk reindex, 7ج)."""
        raise NotImplementedError
