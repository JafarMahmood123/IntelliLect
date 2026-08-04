"""Internal knowledge-base statistics endpoint (super-admin, step 5).

Aggregate figures for a classroom or the whole platform: document counts by status,
total indexed chunks, failure count, and storage consumed. Secret-guarded like the
other /api/internal routes.
"""

from __future__ import annotations

from uuid import UUID

from fastapi import APIRouter, Depends, Query

from app.api.dependencies import DocumentRepositoryDep, require_internal_secret
from app.application.dtos.document_dtos import KnowledgeStatsResponse

router = APIRouter(
    prefix="/api/internal/knowledge",
    tags=["internal-knowledge"],
    dependencies=[Depends(require_internal_secret)],
)


@router.get("/stats", response_model=KnowledgeStatsResponse, response_model_by_alias=True)
async def get_stats(
    documents: DocumentRepositoryDep,
    classroom_id: UUID | None = Query(None, alias="classroomId"),
) -> KnowledgeStatsResponse:
    stats = await documents.stats(classroom_id)
    return KnowledgeStatsResponse(
        classroom_id=classroom_id,
        document_count=stats.document_count,
        status_counts=stats.status_counts,
        total_chunks=stats.total_chunks,
        failed_count=stats.failed_count,
        storage_bytes=stats.storage_bytes,
    )
