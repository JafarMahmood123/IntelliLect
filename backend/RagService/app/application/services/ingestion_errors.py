from __future__ import annotations

from app.application.ports.extractor import ExtractionError


class IngestionError(Exception):
    """Base class for ingestion failures with a known transient/permanent nature."""


class TransientIngestionError(IngestionError):
    """A failure worth retrying: S3 read hiccup, Ollama timeout/5xx/connection error."""


class PermanentIngestionError(IngestionError):
    """A failure that will not succeed on retry: corrupt/unsupported/unreadable file."""


def is_transient(exc: BaseException) -> bool:
    """Classify an exception as transient (retry) or permanent (fail fast).

    Explicitly-typed ingestion errors win. A corrupt/unsupported file surfaces as an
    ExtractionError (Phase 2) — permanent. Everything else defaults to transient, so
    infrastructure hiccups (S3/Ollama) get retried and only give up after the attempt
    budget is exhausted.
    """
    if isinstance(exc, PermanentIngestionError):
        return False
    if isinstance(exc, TransientIngestionError):
        return True
    if isinstance(exc, ExtractionError):
        return False
    return True
