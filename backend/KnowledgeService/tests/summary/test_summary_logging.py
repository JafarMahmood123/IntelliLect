"""Logging privacy (S-5): the summary pipeline emits lifecycle events but NEVER leaks
transcript or generated-summary text into any log record (message, args, or extras)."""

from __future__ import annotations

import logging
from uuid import uuid4

from app.application.services.summary_pipeline import SummaryPipeline
from app.infrastructure.config.settings import Settings

from tests.summary.fakes import FakeBrainClient, build_summary_generator
from tests.summary.pipeline_fakes import (
    FakePdfRenderer,
    FakeSummaryPublisher,
    FakeSummaryStorage,
)

_SECRET_TRANSCRIPT = "SECRET_TRANSCRIPT_XYZ"
_SECRET_SUMMARY = "SECRET_SUMMARY_ABC"

# > 5 real words so the generator does NOT short-circuit and runs the full path.
_TRANSCRIPT = (
    f"Today the lecture explained many detailed ideas including {_SECRET_TRANSCRIPT} "
    "across several worked examples and follow-up discussion."
)
_SUMMARY_MARKDOWN = (
    "# Session Summary\n\n"
    f"## Overview\nA recap that secretly embeds {_SECRET_SUMMARY} inside it.\n\n"
    "## Key Points\n- Point one.\n"
)

# All lifecycle events expected across the generator + pipeline run.
_EXPECTED_EVENTS = {
    "summary_generation_started",
    "summary_generation_finished",
    "summary_pdf_rendered",
    "summary_artifacts_uploaded",
    "summary_ready_published",
}


def _record_blob(record: logging.LogRecord) -> str:
    """Everything a formatter/handler could surface for one record: the rendered
    message plus every attribute value (args + `extra=` fields live in __dict__)."""
    parts = [record.getMessage(), str(record.args)]
    parts.extend(str(value) for value in record.__dict__.values())
    return " ".join(parts)


async def test_full_run_logs_lifecycle_events_without_leaking_secrets(caplog):
    generator, _tc, _repo, _brain = build_summary_generator(
        transcript_text=_TRANSCRIPT,
        classroom_id=uuid4(),
        brain=FakeBrainClient(markdown=_SUMMARY_MARKDOWN),
    )
    pipeline = SummaryPipeline(
        generator,
        FakePdfRenderer(),
        FakeSummaryStorage(),
        FakeSummaryPublisher(),
        Settings(),
    )

    with caplog.at_level(logging.INFO):
        message = await pipeline.run(uuid4())

    assert message.succeeded is True

    # All lifecycle events were logged (some from the generator, some the pipeline).
    logged_events = {record.getMessage() for record in caplog.records}
    missing = _EXPECTED_EVENTS - logged_events
    assert not missing, f"missing lifecycle log events: {missing}"

    # NEITHER secret phrase appears anywhere in ANY captured record.
    for record in caplog.records:
        blob = _record_blob(record)
        assert _SECRET_TRANSCRIPT not in blob, f"transcript leaked in: {record.getMessage()!r}"
        assert _SECRET_SUMMARY not in blob, f"summary leaked in: {record.getMessage()!r}"
