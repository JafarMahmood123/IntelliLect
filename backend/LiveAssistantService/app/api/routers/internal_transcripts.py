"""Internal transcript endpoint (S-0).

Exposes a session's assembled, ordered transcript so the summary feature
(RagService, S-1) can pull it once the session ends. Read-only and secured by
``INTERNAL_API_SECRET`` (``X-Internal-Secret``) — the SAME guard as the other
``/api/internal`` routes. This phase only EXPOSES the transcript; it builds no summary.
"""

from __future__ import annotations

from uuid import UUID

from fastapi import APIRouter, Depends, HTTPException, Response, status
from pydantic import BaseModel

from app.api.dependencies import (
    FeedbackRecorderDep,
    TranscriptRepositoryDep,
    require_internal_secret,
)

router = APIRouter(
    prefix="/api/internal/sessions",
    tags=["transcripts"],
    dependencies=[Depends(require_internal_secret)],
)

# Classroom-scoped transcript maintenance lives under its own prefix but shares the guard.
classrooms_router = APIRouter(
    prefix="/api/internal/classrooms",
    tags=["transcripts"],
    dependencies=[Depends(require_internal_secret)],
)


class DeleteClassroomTranscriptsResponse(BaseModel):
    classroomId: UUID
    transcriptsDeleted: int


class TranscriptResponse(BaseModel):
    """Assembled transcript payload (camelCase for the .NET / RagService caller)."""

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


@router.get("/{session_id}/feedback")
async def get_feedback(
    session_id: UUID, recorder: FeedbackRecorderDep
) -> dict:
    """Return the feedback suggestions delivered for a session (integration testing).

    Populated only when the agent runs with ``AGENT_AUDIO_SOURCE=fake`` (feedback is
    recorded in-process instead of published over LiveKit). Each item is the same
    versioned payload the teacher's frontend would receive. Empty list if none yet /
    not in fake mode.
    """
    items = recorder.get(session_id)
    return {"sessionId": str(session_id), "count": len(items), "feedback": items}


@router.delete("/{session_id}/transcript", status_code=status.HTTP_204_NO_CONTENT)
async def delete_transcript(
    session_id: UUID, repository: TranscriptRepositoryDep
) -> Response:
    """Delete a session's transcript (header + segments).

    Called when the super admin deletes a session and its outputs. Idempotent: a session
    with no transcript still returns 204, so a re-run of a partially-completed deletion or
    a session that was never transcribed is a success, not a 404 (alternate path 6أ).
    """
    await repository.delete_by_session(session_id)
    return Response(status_code=status.HTTP_204_NO_CONTENT)


@classrooms_router.delete(
    "/{classroom_id}/transcripts",
    response_model=DeleteClassroomTranscriptsResponse,
)
async def delete_classroom_transcripts(
    classroom_id: UUID, repository: TranscriptRepositoryDep
) -> DeleteClassroomTranscriptsResponse:
    """Delete every transcript for a classroom. Used when a whole classroom is deleted so
    its sessions' transcripts do not outlive it. Idempotent — reports how many were removed."""
    removed = await repository.delete_by_classroom(classroom_id)
    return DeleteClassroomTranscriptsResponse(
        classroomId=classroom_id, transcriptsDeleted=removed
    )
