from __future__ import annotations

from collections.abc import Sequence
from datetime import datetime
from uuid import UUID

from sqlalchemy import delete, func, select
from sqlalchemy.dialects.postgresql import insert as pg_insert
from sqlalchemy.ext.asyncio import AsyncSession

from app.application.ports.document_repository import (
    DocumentListItem,
    DocumentRepository,
    KnowledgeStats,
)
from app.domain.entities.document import Document
from app.domain.enums.document_status import DocumentStatus
from app.infrastructure.persistence.models import ChunkModel, DocumentModel


class SqlAlchemyDocumentRepository(DocumentRepository):
    """DocumentRepository backed by PostgreSQL via async SQLAlchemy.

    Contains no business logic — only mapping between the Document domain entity
    and its ORM model, plus the queries.
    """

    def __init__(self, session: AsyncSession) -> None:
        self._session = session

    async def add(self, document: Document) -> Document:
        # Idempotent on file_id: a repeat ingest of the same file updates the row
        # in place. Note: attempts / processing_started_at are intentionally NOT in
        # the conflict SET, so a status transition (e.g. -> Done) preserves them.
        stmt = (
            pg_insert(DocumentModel)
            .values(
                id=document.id,
                classroom_id=document.classroom_id,
                file_id=document.file_id,
                s3_key=document.s3_key,
                file_name=document.file_name,
                content_type=document.content_type,
                size_bytes=document.size_bytes,
                content_hash=document.content_hash,
                status=document.status.value,
                last_error=document.last_error,
                attempts=document.attempts,
                processing_started_at=document.processing_started_at,
            )
            .on_conflict_do_update(
                index_elements=[DocumentModel.file_id],
                set_={
                    "s3_key": document.s3_key,
                    "file_name": document.file_name,
                    "content_type": document.content_type,
                    "size_bytes": document.size_bytes,
                    "content_hash": document.content_hash,
                    "status": document.status.value,
                    "last_error": document.last_error,
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
        self, file_id: UUID, status: DocumentStatus, last_error: str | None = None
    ) -> None:
        stmt = select(DocumentModel).where(DocumentModel.file_id == file_id)
        model = (await self._session.execute(stmt)).scalar_one_or_none()
        if model is None:
            return
        model.status = status.value
        model.last_error = last_error
        await self._session.flush()

    async def delete_by_file_id(self, file_id: UUID) -> bool:
        stmt = delete(DocumentModel).where(DocumentModel.file_id == file_id)
        result = await self._session.execute(stmt)
        await self._session.flush()
        return (result.rowcount or 0) > 0

    async def delete_by_classroom_id(self, classroom_id: UUID) -> int:
        stmt = delete(DocumentModel).where(DocumentModel.classroom_id == classroom_id)
        result = await self._session.execute(stmt)
        await self._session.flush()
        return result.rowcount or 0

    async def claim_for_processing(
        self, document: Document, now: datetime
    ) -> Document | None:
        # Atomic claim-or-create: INSERT as Processing, or (on conflict) transition an
        # existing Pending row to Processing. The WHERE guard means a row already in
        # Processing/Done/Failed is NOT updated and NOT returned -> None. The unique
        # index on file_id also serializes a concurrent create, so the claim is
        # race-safe against a not-yet-committed Pending row.
        stmt = (
            pg_insert(DocumentModel)
            .values(
                id=document.id,
                classroom_id=document.classroom_id,
                file_id=document.file_id,
                s3_key=document.s3_key,
                file_name=document.file_name,
                content_type=document.content_type,
                size_bytes=document.size_bytes,
                content_hash=None,
                status=DocumentStatus.PROCESSING.value,
                last_error=None,
                attempts=1,
                processing_started_at=now,
            )
            .on_conflict_do_update(
                index_elements=[DocumentModel.file_id],
                set_={
                    "status": DocumentStatus.PROCESSING.value,
                    "processing_started_at": now,
                    "attempts": DocumentModel.attempts + 1,
                    "last_error": None,
                },
                where=DocumentModel.status == DocumentStatus.PENDING.value,
            )
            .returning(DocumentModel.id)
        )
        claimed_id = (await self._session.execute(stmt)).scalar_one_or_none()
        if claimed_id is None:
            await self._session.flush()
            return None
        model = (
            await self._session.execute(
                select(DocumentModel).where(DocumentModel.id == claimed_id)
            )
        ).scalar_one()
        await self._session.flush()
        return self._to_entity(model)

    async def find_stale_processing(self, threshold: datetime) -> list[Document]:
        stmt = select(DocumentModel).where(
            DocumentModel.status == DocumentStatus.PROCESSING.value,
            DocumentModel.processing_started_at.is_not(None),
            DocumentModel.processing_started_at < threshold,
        )
        models = (await self._session.execute(stmt)).scalars().all()
        return [self._to_entity(model) for model in models]

    async def reset_to_pending(
        self, file_id: UUID, *, reset_attempts: bool
    ) -> Document | None:
        stmt = select(DocumentModel).where(DocumentModel.file_id == file_id)
        model = (await self._session.execute(stmt)).scalar_one_or_none()
        if model is None:
            return None
        model.status = DocumentStatus.PENDING.value
        model.last_error = None
        model.processing_started_at = None
        if reset_attempts:
            model.attempts = 0
        await self._session.flush()
        return self._to_entity(model)

    # --- Super-admin knowledge-base management (read side) ---

    async def list_paged(
        self,
        *,
        page: int,
        page_size: int,
        status: DocumentStatus | None,
        classroom_id: UUID | None,
        search: str | None,
    ) -> tuple[list[DocumentListItem], int]:
        # Chunk count per document via a correlated scalar subquery, so a document with no
        # chunks still appears (count 0).
        chunk_count = (
            select(func.count(ChunkModel.id))
            .where(ChunkModel.document_id == DocumentModel.id)
            .correlate(DocumentModel)
            .scalar_subquery()
        )

        filters = []
        if status is not None:
            filters.append(DocumentModel.status == status.value)
        if classroom_id is not None:
            filters.append(DocumentModel.classroom_id == classroom_id)
        if search:
            filters.append(DocumentModel.file_name.ilike(f"%{search}%"))

        total = (
            await self._session.execute(
                select(func.count()).select_from(DocumentModel).where(*filters)
            )
        ).scalar_one()

        rows = (
            await self._session.execute(
                select(DocumentModel, chunk_count.label("chunk_count"))
                .where(*filters)
                .order_by(DocumentModel.created_at_utc.desc(), DocumentModel.id.desc())
                .offset((page - 1) * page_size)
                .limit(page_size)
            )
        ).all()

        items = [self._to_list_item(model, count) for model, count in rows]
        return items, total

    async def get_statuses(self, file_ids: Sequence[UUID]) -> list[DocumentListItem]:
        if not file_ids:
            return []
        chunk_count = (
            select(func.count(ChunkModel.id))
            .where(ChunkModel.document_id == DocumentModel.id)
            .correlate(DocumentModel)
            .scalar_subquery()
        )
        rows = (
            await self._session.execute(
                select(DocumentModel, chunk_count.label("chunk_count")).where(
                    DocumentModel.file_id.in_(list(file_ids))
                )
            )
        ).all()
        return [self._to_list_item(model, count) for model, count in rows]

    async def stats(self, classroom_id: UUID | None) -> KnowledgeStats:
        doc_filter = (
            [DocumentModel.classroom_id == classroom_id] if classroom_id is not None else []
        )

        status_rows = (
            await self._session.execute(
                select(DocumentModel.status, func.count(), func.coalesce(func.sum(DocumentModel.size_bytes), 0))
                .where(*doc_filter)
                .group_by(DocumentModel.status)
            )
        ).all()

        status_counts: dict[str, int] = {}
        document_count = 0
        storage_bytes = 0
        failed_count = 0
        for status_value, count, size_sum in status_rows:
            status_counts[status_value] = count
            document_count += count
            storage_bytes += int(size_sum or 0)
            if status_value == DocumentStatus.FAILED.value:
                failed_count = count

        chunk_filter = (
            [ChunkModel.classroom_id == classroom_id] if classroom_id is not None else []
        )
        total_chunks = (
            await self._session.execute(
                select(func.count(ChunkModel.id)).where(*chunk_filter)
            )
        ).scalar_one()

        return KnowledgeStats(
            document_count=document_count,
            status_counts=status_counts,
            total_chunks=total_chunks,
            failed_count=failed_count,
            storage_bytes=storage_bytes,
        )

    async def list_file_ids_for_reindex(
        self, classroom_id: UUID, *, failed_only: bool
    ) -> list[UUID]:
        filters = [DocumentModel.classroom_id == classroom_id]
        if failed_only:
            filters.append(DocumentModel.status == DocumentStatus.FAILED.value)
        rows = (
            await self._session.execute(
                select(DocumentModel.file_id).where(*filters)
            )
        ).scalars().all()
        return list(rows)

    async def count_active(self, classroom_id: UUID) -> int:
        active = (DocumentStatus.PENDING.value, DocumentStatus.PROCESSING.value)
        return (
            await self._session.execute(
                select(func.count())
                .select_from(DocumentModel)
                .where(
                    DocumentModel.classroom_id == classroom_id,
                    DocumentModel.status.in_(active),
                )
            )
        ).scalar_one()

    @staticmethod
    def _to_list_item(model: DocumentModel, chunk_count: int) -> DocumentListItem:
        return DocumentListItem(
            file_id=model.file_id,
            classroom_id=model.classroom_id,
            file_name=model.file_name,
            content_type=model.content_type,
            size_bytes=model.size_bytes,
            status=DocumentStatus(model.status),
            attempts=model.attempts,
            chunk_count=chunk_count,
        )

    @staticmethod
    def _to_entity(model: DocumentModel) -> Document:
        return Document(
            id=model.id,
            classroom_id=model.classroom_id,
            file_id=model.file_id,
            s3_key=model.s3_key,
            file_name=model.file_name,
            content_type=model.content_type,
            size_bytes=model.size_bytes,
            content_hash=model.content_hash,
            status=DocumentStatus(model.status),
            last_error=model.last_error,
            attempts=model.attempts,
            processing_started_at=model.processing_started_at,
            created_at_utc=model.created_at_utc,
            updated_at_utc=model.updated_at_utc,
        )
