from abc import ABC, abstractmethod
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
        self, file_id: UUID, status: DocumentStatus, error: str | None = None
    ) -> None:
        raise NotImplementedError

    @abstractmethod
    async def delete_by_file_id(self, file_id: UUID) -> bool:
        """Delete the document. Returns True if a row was removed."""
        raise NotImplementedError
