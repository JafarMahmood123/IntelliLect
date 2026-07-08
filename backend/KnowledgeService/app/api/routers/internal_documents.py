from __future__ import annotations

from uuid import UUID

from fastapi import APIRouter, Depends, Response, status

from app.api.dependencies import DocumentRepositoryDep, require_internal_secret
from app.application.dtos.document_dtos import IngestDocumentRequest, IngestDocumentResponse
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
) -> IngestDocumentResponse:
    """Register a document for future processing.

    Foundation behavior only: upsert a Pending row (idempotent on fileId) and
    return 202. Actual download/extract/OCR/chunk/embed happens in later work.
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
    return IngestDocumentResponse(
        document_id=saved.id,
        file_id=saved.file_id,
        status=saved.status.value,
    )


@router.delete("/{file_id}", status_code=status.HTTP_204_NO_CONTENT)
async def delete_document(
    file_id: UUID,
    documents: DocumentRepositoryDep,
) -> Response:
    """Delete a document. Its chunks are removed via ON DELETE CASCADE."""
    await documents.delete_by_file_id(file_id)
    return Response(status_code=status.HTTP_204_NO_CONTENT)
