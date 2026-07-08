from __future__ import annotations

from fastapi import APIRouter
from fastapi.responses import JSONResponse
from sqlalchemy import text

from app.infrastructure.persistence.database import get_session_factory

router = APIRouter(tags=["health"])


@router.get("/health")
async def health() -> JSONResponse:
    """Liveness + DB readiness. Verifies a `SELECT 1` against PostgreSQL."""
    db_ok = False
    try:
        factory = get_session_factory()
        async with factory() as session:
            await session.execute(text("SELECT 1"))
        db_ok = True
    except Exception:
        db_ok = False

    status_code = 200 if db_ok else 503
    return JSONResponse(
        status_code=status_code,
        content={"status": "ok" if db_ok else "degraded", "db": "ok" if db_ok else "fail"},
    )
