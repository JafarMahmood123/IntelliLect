from __future__ import annotations

from fastapi import APIRouter, Depends, HTTPException, status

from app.api.dependencies import RetrievalServiceDep, require_internal_secret
from app.application.dtos.search_dtos import SearchRequest, SearchResponse

# Secured with the internal shared secret for now — this is consumed service-to-service.
# When a user-facing feature calls it, swap this for user-JWT auth plus a server-side
# classroom-membership check (see the README).
router = APIRouter(
    prefix="/api/search",
    tags=["search"],
    dependencies=[Depends(require_internal_secret)],
)


@router.post(
    "",
    response_model=SearchResponse,
    response_model_by_alias=True,
)
async def search(
    payload: SearchRequest,
    retrieval: RetrievalServiceDep,
) -> SearchResponse:
    """Retrieve the top-k most similar chunks for a query within one classroom.

    Returns chunks only (text + metadata + similarity score), never a generated
    answer. Malformed bodies are rejected by validation with 422.
    """
    try:
        return await retrieval.search(payload)
    except ValueError as exc:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail=str(exc),
        ) from exc
