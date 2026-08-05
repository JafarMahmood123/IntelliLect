"""Internal re-embedding endpoints (secret-guarded, operator-triggered).

Changing the embedding provider/model/dimension requires an Alembic migration that DROPS every
stored vector — vectors from two models are not comparable, so there is nothing to convert. These
endpoints refill them from the chunk text already in the database, so a model change no longer
means re-uploading every document by hand.

Resumable by construction: ``embedding IS NULL`` is the pending marker, which is exactly what the
migration leaves behind. A cancelled or crashed sweep is recovered by POSTing again.
"""

from __future__ import annotations

from fastapi import APIRouter, Depends, Response, status

from app.api.dependencies import ReembedRunnerDep, require_internal_secret

router = APIRouter(
    prefix="/api/internal/reembed",
    tags=["internal-reembed"],
    dependencies=[Depends(require_internal_secret)],
)


@router.post("", status_code=status.HTTP_202_ACCEPTED)
async def start_reembed(runner: ReembedRunnerDep, response: Response) -> dict:
    """Start a sweep. 202 if launched, 409 if one is already running.

    Never queues a second run: two concurrent sweeps would race for the same NULL rows and
    embed chunks twice, paying for every duplicate.

    The refusal has to be visible in the STATUS, not only in the body. This is a curl-and-script
    endpoint; the obvious way to drive it is `-f` or a `resp.ok` check, and a 202 for a run that
    did not start reads as a second sweep having been launched.
    """
    started = runner.start()
    if not started:
        response.status_code = status.HTTP_409_CONFLICT
        return {"status": "already-running", **runner.progress().as_dict()}
    return {"status": "accepted", **runner.progress().as_dict()}


@router.get("/status")
async def reembed_status(runner: ReembedRunnerDep) -> dict:
    """Progress of the current or last sweep.

    ``remaining`` is read from the database rather than an in-memory counter, so it stays
    truthful even if the service restarted partway through a run.
    """
    return {"running": runner.is_running(), **runner.progress().as_dict()}
