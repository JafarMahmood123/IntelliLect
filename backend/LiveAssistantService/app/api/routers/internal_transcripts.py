"""Internal transcript endpoint (S-0).

Exposes a session's assembled, ordered transcript so the summary feature
(KnowledgeService, S-1) can pull it once the session ends. Read-only and secured by
``INTERNAL_API_SECRET`` (``X-Internal-Secret``) — the SAME guard as the other
``/api/internal`` routes. This phase only EXPOSES the transcript; it builds no summary.
"""

from __future__ import annotations

from uuid import UUID

from fastapi import APIRouter, Depends, HTTPException, status
from pydantic import BaseModel

from app.api.dependencies import TranscriptRepositoryDep, require_internal_secret

router = APIRouter(
    prefix="/api/internal/sessions",
    tags=["transcripts"],
    dependencies=[Depends(require_internal_secret)],
)


class TranscriptResponse(BaseModel):
    """Assembled transcript payload (camelCase for the .NET / KnowledgeService caller)."""

    sessionId: UUID
    classroomId: UUID
    status: str
    segmentCount: int
    text: str


@router.get("/{session_id}/transcript", response_model=TranscriptResponse)
async def get_transcript(
    session_id: UUID, repository: TranscriptRepositoryDep
) -> TranscriptResponse:
    """Return the assembled, ordered transcript for a session. 404 if unknown."""
    header = await repository.get_session_transcript(session_id)
    if header is None:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"No transcript for session {session_id}.",
        )
    segments = await repository.get_transcript(session_id)
    text = await repository.assemble_text(session_id)
    return TranscriptResponse(
        sessionId=header.session_id,
        classroomId=header.classroom_id,
        status=header.status.value,
        segmentCount=len(segments),
        text=text,
    )
