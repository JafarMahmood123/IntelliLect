from __future__ import annotations

from uuid import UUID

from sqlalchemy import delete, select
from sqlalchemy.dialects.postgresql import insert as pg_insert
from sqlalchemy.ext.asyncio import AsyncSession

from app.application.ports.document_repository import DocumentRepository
from app.domain.entities.document import Document
from app.domain.enums.document_status import DocumentStatus
from app.infrastructure.persistence.models import DocumentModel


class SqlAlchemyDocumentRepository(DocumentRepository):
    """DocumentRepository backed by PostgreSQL via async SQLAlchemy.

    Contains no business logic — only mapping between the Document domain entity
    and its ORM model, plus the queries.
    """

    def __init__(self, session: AsyncSession) -> None:
        self._session = session

    async def add(self, document: Document) -> Document:
        # Idempotent on file_id: a repeat ingest of the same file updates the row
        # in place rather than raising a unique-constraint error.
        stmt = (
            pg_insert(DocumentModel)
            .values(
                id=document.id,
                classroom_id=document.classroom_id,
                file_id=document.file_id,
                s3_key=document.s3_key,
                file_name=document.file_name,
                content_type=document.content_type,
                content_hash=document.content_hash,
                status=document.status.value,
                error=document.error,
            )
            .on_conflict_do_update(
                index_elements=[DocumentModel.file_id],
                set_={
                    "s3_key": document.s3_key,
                    "file_name": document.file_name,
                    "content_type": document.content_type,
                    "content_hash": document.content_hash,
                    "status": document.status.value,
                    "error": document.error,
                },
            )
            .returning(DocumentModel.id)
        )
        result = await self._session.execute(stmt)
        document.id = result.scalar_one()
        await self._session.flush()
        return document

    async def get_by_file_id(self, file_id: UUID) -> Document | None:
        stmt = select(DocumentModel).where(DocumentModel.file_id == file_id)
        model = (await self._session.execute(stmt)).scalar_one_or_none()
        return self._to_entity(model) if model is not None else None

    async def update_status(
        self, file_id: UUID, status: DocumentStatus, error: str | None = None
    ) -> None:
        stmt = select(DocumentModel).where(DocumentModel.file_id == file_id)
        model = (await self._session.execute(stmt)).scalar_one_or_none()
        if model is None:
            return
        model.status = status.value
        model.error = error
        await self._session.flush()

    async def delete_by_file_id(self, file_id: UUID) -> bool:
        stmt = delete(DocumentModel).where(DocumentModel.file_id == file_id)
        result = await self._session.execute(stmt)
        await self._session.flush()
        return (result.rowcount or 0) > 0

    @staticmethod
    def _to_entity(model: DocumentModel) -> Document:
        return Document(
            id=model.id,
            classroom_id=model.classroom_id,
            file_id=model.file_id,
            s3_key=model.s3_key,
            file_name=model.file_name,
            content_type=model.content_type,
            content_hash=model.content_hash,
            status=DocumentStatus(model.status),
            error=model.error,
            created_at_utc=model.created_at_utc,
            updated_at_utc=model.updated_at_utc,
        )
