"""Internal session-lifecycle endpoints (LA-6), triggered by the .NET side.

Secured by ``INTERNAL_API_SECRET`` (``X-Internal-Secret``). The StreamingService
calls these after a LiveKit room is created (start) and when the session ends (stop),
so exactly one agent pipeline runs per live session.
"""

from __future__ import annotations

from uuid import UUID

from fastapi import APIRouter, Depends, HTTPException, Response, status
from pydantic import BaseModel, ConfigDict, Field

from app.api.dependencies import SessionManagerDep, require_internal_secret
from app.application.services.session_manager import SessionCapacityError
from app.domain.entities.session_context import SessionContext

router = APIRouter(
    prefix="/api/internal/sessions",
    tags=["sessions"],
    dependencies=[Depends(require_internal_secret)],
)


class StartSessionRequest(BaseModel):
    """Inbound start payload (camelCase from the .NET caller)."""

    model_config = ConfigDict(populate_by_name=True)

    session_id: UUID = Field(alias="sessionId")
    classroom_id: UUID = Field(alias="classroomId")
    room_name: str = Field(alias="roomName")
    teacher_identity: str = Field(alias="teacherIdentity")


@router.post("/start", status_code=status.HTTP_202_ACCEPTED)
async def start_session(payload: StartSessionRequest, manager: SessionManagerDep) -> dict:
    """Launch (or no-op if already active) the agent pipeline for a session.

    Returns 202 immediately — the pipeline joins the room and captures in the
    background. Idempotent on ``sessionId``; 503 if the concurrency cap is reached.
    """
    session = SessionContext(
        session_id=payload.session_id,
        classroom_id=payload.classroom_id,
        teacher_identity=payload.teacher_identity,
        room_name=payload.room_name,
    )
    try:
        await manager.start(session)
    except SessionCapacityError as exc:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail=str(exc)
        ) from exc
    return {"status": "accepted", "sessionId": str(payload.session_id)}


@router.post("/{session_id}/stop", status_code=status.HTTP_204_NO_CONTENT)
async def stop_session(session_id: UUID, manager: SessionManagerDep) -> Response:
    """Tear down the session's pipeline. Idempotent (unknown session -> still 204)."""
    await manager.stop(session_id)
    return Response(status_code=status.HTTP_204_NO_CONTENT)


@router.get("")
async def list_sessions(manager: SessionManagerDep) -> dict:
    """List active session ids (ops/debug)."""
    return {
        "activeSessions": [str(sid) for sid in manager.active_session_ids()],
        "count": manager.active_count(),
    }
