from __future__ import annotations

from fastapi import APIRouter, Depends, HTTPException, status

from app.api.dependencies import AnswerServiceDep, require_internal_secret
from app.application.dtos.answer_dtos import AnswerRequest, AnswerResponse

# Secured with the internal shared secret for now — consumed service-to-service.
# When a user-facing feature calls it, swap this for user-JWT auth PLUS a server-side
# classroom-membership check so a user can't ask about a classroom they're not in
# (see the README).
router = APIRouter(
    prefix="/api/answer",
    tags=["answer"],
    dependencies=[Depends(require_internal_secret)],
)


@router.post(
    "",
    response_model=AnswerResponse,
    response_model_by_alias=True,
)
async def answer(
    payload: AnswerRequest,
    service: AnswerServiceDep,
) -> AnswerResponse:
    """Answer a question grounded in one classroom's retrieved chunks.

    Retrieves the top-k relevant chunks, packs a numbered context, and asks the local
    model to answer using only that context (citing sources by [n]). Malformed bodies
    are rejected with 422.
    """
    try:
        return await service.answer(payload.classroom_id, payload.question, payload.top_k)
    except ValueError as exc:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail=str(exc),
        ) from exc
