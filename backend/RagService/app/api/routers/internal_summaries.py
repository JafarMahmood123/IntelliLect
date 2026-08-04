from __future__ import annotations

from uuid import UUID

from fastapi import APIRouter, Depends, status

from app.api.dependencies import (
    SettingsDep,
    SummaryRunnerDep,
    require_internal_secret,
)
from app.application.dtos.summary_dtos import SummarizeRequest, SummarizeResponse

# The session-end summary trigger. Secured by the internal shared secret — called by
# the service that ends sessions (or LiveAssistant on pipeline stop).
router = APIRouter(
    prefix="/api/internal/sessions",
    tags=["internal-summaries"],
    dependencies=[Depends(require_internal_secret)],
)


@router.post(
    "/{session_id}/summarize",
    status_code=status.HTTP_202_ACCEPTED,
    response_model=SummarizeResponse,
    response_model_by_alias=True,
)
async def summarize_session(
    session_id: UUID,
    payload: SummarizeRequest,
    settings: SettingsDep,
    runner: SummaryRunnerDep,
) -> SummarizeResponse:
    """Kick off the session-summary pipeline in the background and return 202.

    NON-FATAL and non-blocking: the generate -> render -> upload -> publish work runs on
    a background task, so session end never waits on (or fails from) summarization. When
    ``SUMMARY_TRIGGER_ENABLED`` is false the trigger is a no-op. Idempotent per session —
    a duplicate trigger while one run is in flight is skipped.
    """
    if not settings.summary_trigger_enabled:
        return SummarizeResponse(session_id=session_id, status="skipped")

    started = runner.enqueue(session_id, payload.classroom_id)
    return SummarizeResponse(
        session_id=session_id, status="accepted" if started else "in-progress"
    )
