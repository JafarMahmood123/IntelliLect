from __future__ import annotations

from fastapi import APIRouter, Response

from app.observability import metrics

router = APIRouter(tags=["metrics"])


@router.get("/metrics")
async def prometheus_metrics() -> Response:
    """Prometheus text exposition of all KnowledgeService metrics.

    Only mounted when METRICS_ENABLED (see the app factory).
    """
    payload, content_type = metrics.render()
    return Response(content=payload, media_type=content_type)
