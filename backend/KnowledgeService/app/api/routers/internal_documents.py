from __future__ import annotations

from uuid import UUID

from fastapi import APIRouter, Depends, HTTPException, Response, status

from app.api.dependencies import (
    ChunkRepositoryDep,
    DocumentRepositoryDep,
    IngestionWorkerDep,
    require_internal_secret,
)
from app.application.dtos.document_dtos import (
    DocumentStatusResponse,
    IngestDocumentRequest,
    IngestDocumentResponse,
)
from app.application.services.ingestion_service import IngestionJob
from app.domain.entities.document import Document
from app.domain.enums.document_status import DocumentStatus

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
