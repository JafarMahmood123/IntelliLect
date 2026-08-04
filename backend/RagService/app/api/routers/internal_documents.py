from __future__ import annotations

import asyncio
from uuid import UUID

from fastapi import APIRouter, Depends, HTTPException, Query, Response, status

from app.api.dependencies import (
    ChunkRepositoryDep,
    DocumentRepositoryDep,
    IngestionWorkerDep,
    SettingsDep,
    require_internal_secret,
)
from app.application.dtos.document_dtos import (
    AdminDocumentItem,
    AdminDocumentListResponse,
    BulkReindexResponse,
    DeleteClassroomIndexResponse,
    DocumentDetailResponse,
    DocumentStatusResponse,
    IngestDocumentRequest,
    IngestDocumentResponse,
    StatusBatchRequest,
)
from app.application.ports.document_repository import DocumentListItem
from app.application.services.ingestion_service import IngestionJob
from app.domain.entities.document import Document
from app.domain.enums.document_status import DocumentStatus


def _parse_status(value: str | None) -> DocumentStatus | None:
    if not value:
        return None
    try:
        return DocumentStatus(value)
    except ValueError:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"Unknown indexing status '{value}'.",
        )


def _to_item(item: DocumentListItem) -> AdminDocumentItem:
    return AdminDocumentItem(
        file_id=item.file_id,
        classroom_id=item.classroom_id,
        file_name=item.file_name,
        content_type=item.content_type,
        size_bytes=item.size_bytes,
        status=item.status.value,
        attempts=item.attempts,
        chunk_count=item.chunk_count,
    )

# All routes here require the internal shared secret.
router = APIRouter(
    prefix="/api/internal/documents",
    tags=["internal-documents"],
    dependencies=[Depends(require_internal_secret)],
)


@router.post(
    "/ingest",
    status_code=status.HTTP_202_ACCEPTED,
    response_model=IngestDocumentResponse,
    response_model_by_alias=True,
)
async def ingest_document(
    payload: IngestDocumentRequest,
    documents: DocumentRepositoryDep,
    worker: IngestionWorkerDep,
) -> IngestDocumentResponse:
    """Register a document and enqueue it for background ingestion.

    Upserts a Pending row (idempotent on fileId), enqueues the ingestion job, and
    returns 202. If the bounded queue is full, returns 503 so the caller can retry.
    """
    document = Document(
        classroom_id=payload.classroom_id,
        file_id=payload.file_id,
        s3_key=payload.s3_key,
        file_name=payload.file_name,
        content_type=payload.content_type,
        size_bytes=payload.size_bytes,
        status=DocumentStatus.PENDING,
    )
    saved = await documents.add(document)

    if not worker.enqueue(IngestionJob.from_document(saved)):
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="Ingestion queue is full; please retry shortly.",
        )

    return IngestDocumentResponse(
        document_id=saved.id,
        file_id=saved.file_id,
        status=saved.status.value,
    )


@router.post(
    "/{file_id}/reindex",
    status_code=status.HTTP_202_ACCEPTED,
    response_model=IngestDocumentResponse,
    response_model_by_alias=True,
)
async def reindex_document(
    file_id: UUID,
    documents: DocumentRepositoryDep,
    worker: IngestionWorkerDep,
) -> IngestDocumentResponse:
    """Manually re-trigger ingestion for a document.

    Resets it to Pending (clearing last_error and the attempt count) and re-enqueues
    it through the existing pipeline. Covers missed upload triggers and manual
    recovery of Failed/stuck documents. 404 if the document is unknown; 503 if the
    queue is full.
    """
    document = await documents.reset_to_pending(file_id, reset_attempts=True)
    if document is None:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"No document with fileId {file_id}.",
        )

    if not worker.enqueue(IngestionJob.from_document(document)):
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="Ingestion queue is full; please retry shortly.",
        )

    return IngestDocumentResponse(
        document_id=document.id,
        file_id=document.file_id,
        status=document.status.value,
    )


@router.get(
    "/{file_id}/status",
    response_model=DocumentStatusResponse,
    response_model_by_alias=True,
)
async def get_document_status(
    file_id: UUID,
    documents: DocumentRepositoryDep,
) -> DocumentStatusResponse:
    """Return a document's indexing status (Pending/Processing/Done/Failed).

    Read-only projection for ClassroomService to proxy to classroom members.
    404 if no document has been registered for this file id yet.
    """
    document = await documents.get_by_file_id(file_id)
    if document is None:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"No document with fileId {file_id}.",
        )

    return DocumentStatusResponse(file_id=document.file_id, status=document.status.value)


@router.get("", response_model=AdminDocumentListResponse, response_model_by_alias=True)
async def list_documents(
    documents: DocumentRepositoryDep,
    settings: SettingsDep,
    page: int = Query(1, ge=1),
    page_size: int = Query(None, alias="pageSize"),
    status_filter: str | None = Query(None, alias="status"),
    classroom_id: UUID | None = Query(None, alias="classroomId"),
    search: str | None = Query(None),
) -> AdminDocumentListResponse:
    """Paged super-admin document list (step 3), driven by RagService when a status
    filter is applied. Each item carries the indexing status, attempts and chunk count."""
    size = page_size or settings.admin_list_default_page_size
    size = max(1, min(size, settings.admin_list_max_page_size))
    parsed = _parse_status(status_filter)

    items, total = await documents.list_paged(
        page=page,
        page_size=size,
        status=parsed,
        classroom_id=classroom_id,
        search=search.strip() if search else None,
    )
    return AdminDocumentListResponse(
        items=[_to_item(i) for i in items], total=total, page=page, page_size=size
    )


@router.post("/status-batch", response_model=list[AdminDocumentItem], response_model_by_alias=True)
async def status_batch(
    payload: StatusBatchRequest, documents: DocumentRepositoryDep
) -> list[AdminDocumentItem]:
    """Batch status/chunk-count lookup for a set of file ids. Enriches a file list whose
    registry lives in ClassroomService; unknown ids are simply omitted."""
    items = await documents.get_statuses(payload.file_ids)
    return [_to_item(i) for i in items]


@router.get("/{file_id}/detail", response_model=DocumentDetailResponse, response_model_by_alias=True)
async def get_document_detail(
    file_id: UUID, documents: DocumentRepositoryDep
) -> DocumentDetailResponse:
    """A failed/other document's diagnostics (step 4): status, attempts and failure reason.
    404 if no document is registered for this file id (7أ)."""
    statuses = await documents.get_statuses([file_id])
    document = await documents.get_by_file_id(file_id)
    if document is None or not statuses:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"No document with fileId {file_id}.",
        )
    item = statuses[0]
    return DocumentDetailResponse(
        file_id=item.file_id,
        classroom_id=item.classroom_id,
        file_name=item.file_name,
        content_type=item.content_type,
        size_bytes=item.size_bytes,
        status=item.status.value,
        attempts=item.attempts,
        chunk_count=item.chunk_count,
        last_error=document.last_error,
    )


@router.post(
    "/classrooms/{classroom_id}/reindex",
    status_code=status.HTTP_202_ACCEPTED,
    response_model=BulkReindexResponse,
    response_model_by_alias=True,
)
async def reindex_classroom(
    classroom_id: UUID,
    documents: DocumentRepositoryDep,
    worker: IngestionWorkerDep,
    settings: SettingsDep,
    failed_only: bool = Query(False, alias="failedOnly"),
) -> BulkReindexResponse:
    """Re-index all of a classroom's documents (optionally only Failed ones).

    Guards: rejects when indexing is already in flight for the classroom (7ج, 409); rejects when
    the file count exceeds the bulk cap (7ب, 400) so the admin narrows to failed-only. During
    enqueue, a momentarily-full queue is retried with a short backoff; files that still cannot be
    enqueued are counted as skipped and reported (7د), never lost silently.
    """
    # 7ج: don't pile a new batch on top of one still draining.
    if await documents.count_active(classroom_id) > 0:
        raise HTTPException(
            status_code=status.HTTP_409_CONFLICT,
            detail="A reindex is already in progress for this classroom. Wait for it to finish.",
        )

    file_ids = await documents.list_file_ids_for_reindex(classroom_id, failed_only=failed_only)

    # 7ب: cap the batch size; suggest narrowing to failed-only.
    if len(file_ids) > settings.reindex_bulk_max:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=(
                f"{len(file_ids)} files exceed the reindex limit of {settings.reindex_bulk_max}. "
                "Narrow the scope (e.g. failed files only)."
            ),
        )

    enqueued = 0
    skipped = 0
    for file_id in file_ids:
        document = await documents.reset_to_pending(file_id, reset_attempts=True)
        if document is None:
            skipped += 1
            continue
        if await _enqueue_with_backoff(worker, document, settings):
            enqueued += 1
        else:
            # 7د: the queue stayed full through the retries — count it, don't lose it.
            skipped += 1

    return BulkReindexResponse(
        classroom_id=classroom_id, requested=len(file_ids), enqueued=enqueued, skipped=skipped
    )


async def _enqueue_with_backoff(worker, document, settings) -> bool:
    for attempt in range(settings.reindex_enqueue_retries):
        if worker.enqueue(IngestionJob.from_document(document)):
            return True
        if attempt < settings.reindex_enqueue_retries - 1:
            await asyncio.sleep(settings.reindex_enqueue_retry_seconds)
    return False


@router.delete(
    "/classrooms/{classroom_id}",
    response_model=DeleteClassroomIndexResponse,
    response_model_by_alias=True,
)
async def delete_classroom_index(
    classroom_id: UUID,
    documents: DocumentRepositoryDep,
    chunks: ChunkRepositoryDep,
) -> DeleteClassroomIndexResponse:
    """De-index an entire classroom: drop all its chunks, then all its documents.

    Called once by ClassroomService when a classroom is deleted, instead of looping
    the per-file DELETE (one round trip per document). Chunks go first so a failure
    between the two statements leaves documents whose chunks are gone rather than
    orphaned chunks that could still surface in search results.

    Idempotent, so the caller can safely re-run a partially-completed deletion: a
    second call finds nothing and reports zero counts.
    """
    chunks_deleted = await chunks.delete_by_classroom_id(classroom_id)
    documents_deleted = await documents.delete_by_classroom_id(classroom_id)
    return DeleteClassroomIndexResponse(
        classroom_id=classroom_id,
        documents_deleted=documents_deleted,
        chunks_deleted=chunks_deleted,
    )


@router.delete("/{file_id}", status_code=status.HTTP_204_NO_CONTENT)
async def delete_document(
    file_id: UUID,
    documents: DocumentRepositoryDep,
    chunks: ChunkRepositoryDep,
) -> Response:
    """Delete a document and its chunks.

    The DB also cascades chunk deletion, but we remove them explicitly so behavior
    is identical regardless of the backing store.
    """
    document = await documents.get_by_file_id(file_id)
    if document is not None:
        await chunks.delete_by_document_id(document.id)
        await documents.delete_by_file_id(file_id)
    return Response(status_code=status.HTTP_204_NO_CONTENT)
