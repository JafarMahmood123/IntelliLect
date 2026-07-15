from __future__ import annotations

import asyncio

import httpx
from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse
from sqlalchemy import text

from app.infrastructure.config.settings import get_settings
from app.infrastructure.persistence.database import get_session_factory
from app.infrastructure.rendering.weasyprint_pdf_renderer import weasyprint_available

router = APIRouter(tags=["health"])

# Ollama reachability is a soft signal; keep its probe snappy so /health stays fast.
_OLLAMA_PROBE_TIMEOUT_SECONDS = 5.0
# Summary-side probes are purely informational; keep them snappy and non-blocking.
_SUMMARY_PROBE_TIMEOUT_SECONDS = 3.0


async def _check_db() -> bool:
    """Verify a `SELECT 1` against PostgreSQL."""
    try:
        factory = get_session_factory()
        async with factory() as session:
            await session.execute(text("SELECT 1"))
        return True
    except Exception:
        return False


async def _check_ollama() -> tuple[bool, list[str]]:
    """Probe host Ollama via `GET /api/tags`. Non-fatal — never raises.

    Returns (reachable, [model names present]) so the caller can also confirm the
    configured models are pulled.
    """
    settings = get_settings()
    headers: dict[str, str] = {}
    if settings.ollama_auth_token:
        headers["Authorization"] = f"Bearer {settings.ollama_auth_token}"
    url = f"{settings.ollama_base_url.rstrip('/')}/api/tags"
    try:
        async with httpx.AsyncClient(
            timeout=_OLLAMA_PROBE_TIMEOUT_SECONDS, headers=headers
        ) as client:
            response = await client.get(url)
        if response.status_code != 200:
            return False, []
        models = [m.get("name", "") for m in response.json().get("models", [])]
        return True, [name for name in models if name]
    except Exception:
        return False, []


def _model_status(reachable: bool, models: list[str], target: str) -> str:
    """"available" / "missing" / "unknown" for a configured model name."""
    if not reachable:
        return "unknown"
    present = any(name == target or name.startswith(f"{target}:") for name in models)
    return "available" if present else "missing"


def _check_pdf_renderer() -> str:
    """"available" / "unavailable" — whether WeasyPrint can actually render a PDF."""
    try:
        return "available" if weasyprint_available() else "unavailable"
    except Exception:
        return "unavailable"


def _head_bucket(bucket: str) -> None:
    """Blocking best-effort HEAD on the summary bucket (run off the event loop)."""
    import boto3  # lazy: only needed when a bucket is configured
    from botocore.config import Config

    settings = get_settings()
    session_kwargs: dict[str, str] = {}
    access_key = settings.summary_s3_access_key or settings.s3_access_key
    secret_key = settings.summary_s3_secret_key or settings.s3_secret_key
    region = settings.summary_s3_region or settings.s3_region
    endpoint = settings.summary_s3_endpoint or settings.s3_service_url
    if access_key:
        session_kwargs["aws_access_key_id"] = access_key
    if secret_key:
        session_kwargs["aws_secret_access_key"] = secret_key
    if region:
        session_kwargs["region_name"] = region
    client = boto3.client(
        "s3",
        endpoint_url=endpoint or None,
        config=Config(
            connect_timeout=_SUMMARY_PROBE_TIMEOUT_SECONDS,
            read_timeout=_SUMMARY_PROBE_TIMEOUT_SECONDS,
            retries={"max_attempts": 0},
        ),
        **session_kwargs,
    )
    client.head_bucket(Bucket=bucket)


async def _check_summary_storage() -> str:
    """"reachable" / "unreachable" / "not-configured" — best-effort bucket HEAD.

    Non-fatal and purely informational: never raises, always resolves quickly.
    """
    settings = get_settings()
    bucket = settings.summary_s3_bucket or settings.s3_bucket_name
    if not bucket:
        return "not-configured"
    try:
        await asyncio.wait_for(
            asyncio.to_thread(_head_bucket, bucket),
            timeout=_SUMMARY_PROBE_TIMEOUT_SECONDS + 1.0,
        )
        return "reachable"
    except Exception:
        return "unreachable"


async def _check_transcript_endpoint() -> str:
    """"reachable" / "unreachable" / "not-configured" — LiveAssistant transcript probe."""
    settings = get_settings()
    base_url = settings.live_assistant_base_url.strip()
    if not base_url:
        return "not-configured"
    url = f"{base_url.rstrip('/')}/health"
    try:
        async with httpx.AsyncClient(timeout=_SUMMARY_PROBE_TIMEOUT_SECONDS) as client:
            response = await client.get(url)
        return "reachable" if response.status_code < 500 else "unreachable"
    except Exception:
        return "unreachable"


def _check_worker(request: Request) -> tuple[bool, int]:
    """Whether the ingestion worker is running, and its current queue depth."""
    worker = getattr(request.app.state, "ingestion_worker", None)
    if worker is None:
        return False, 0
    return worker.is_running(), worker.queue_depth()


@router.get("/health")
async def health(request: Request) -> JSONResponse:
    """Liveness + component readiness.

    Reports each component (db, ollama, generation model, worker) separately. The DB
    check is the liveness-critical one (503 if it fails); the others are non-fatal
    signals, but any unhealthy component makes the overall status "degraded".
    """
    settings = get_settings()
    db_ok = await _check_db()
    ollama_ok, models = await _check_ollama()
    generation_status = _model_status(ollama_ok, models, settings.generation_model)
    worker_ok, queue_depth = _check_worker(request)

    # Summary-side probes are purely informational (never affect overall_ok/status_code).
    pdf_renderer_status = _check_pdf_renderer()
    summary_storage_status = await _check_summary_storage()
    transcript_endpoint_status = await _check_transcript_endpoint()

    status_code = 200 if db_ok else 503
    overall_ok = db_ok and ollama_ok and worker_ok and generation_status == "available"
    return JSONResponse(
        status_code=status_code,
        content={
            "status": "ok" if overall_ok else "degraded",
            "db": "ok" if db_ok else "fail",
            "ollama": "reachable" if ollama_ok else "unreachable",
            "generationModel": generation_status,
            "worker": "running" if worker_ok else "down",
            "queueDepth": queue_depth,
            "pdfRenderer": pdf_renderer_status,
            "summaryStorage": summary_storage_status,
            "transcriptEndpoint": transcript_endpoint_status,
        },
    )
