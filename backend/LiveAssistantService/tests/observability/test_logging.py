"""Structured logging (LA-8): lifecycle events at INFO with correlation, and the hard
privacy rule — transcript/idea/suggestion/chunk text is NEVER logged.
"""

from __future__ import annotations

import logging

from app.observability.logging_config import CorrelationFilter, JsonFormatter
from tests.support.pipeline_harness import (
    CHUNK_TEXT,
    IDEA_PHRASE,
    SUGGESTION_TEXT,
    run_scenario,
)


class _CaptureHandler(logging.Handler):
    def __init__(self) -> None:
        super().__init__(level=logging.DEBUG)
        self.addFilter(CorrelationFilter())  # populate session_id/run_id like production
        self.records: list[logging.LogRecord] = []

    def emit(self, record: logging.LogRecord) -> None:
        self.records.append(record)


async def _run_capturing():
    handler = _CaptureHandler()
    root = logging.getLogger()
    previous = root.level
    root.addHandler(handler)
    root.setLevel(logging.DEBUG)
    try:
        session, sink, brain = await run_scenario()
    finally:
        root.removeHandler(handler)
        root.setLevel(previous)
    return session, handler.records


async def test_lifecycle_events_logged_at_info_with_correlation():
    session, records = await _run_capturing()
    by_msg = {r.getMessage(): r for r in records}

    for event in ("session_started", "agent_joined", "idea_completed", "evaluation",
                  "feedback_delivered", "agent_left", "session_ended"):
        assert event in by_msg, f"missing lifecycle event: {event}"
        assert by_msg[event].levelno == logging.INFO

    # Every lifecycle record carries the session correlation id.
    lifecycle = [r for r in records if r.getMessage() in {"session_started", "idea_completed", "session_ended"}]
    assert lifecycle and all(r.session_id == str(session.session_id) for r in lifecycle)

    # idea_completed carries structured counts (never text).
    idea = by_msg["idea_completed"]
    assert isinstance(getattr(idea, "trigger"), str)
    assert isinstance(getattr(idea, "tokens"), int)
    assert isinstance(getattr(idea, "duration_ms"), int)


async def test_no_transcript_idea_suggestion_or_chunk_text_is_ever_logged():
    _session, records = await _run_capturing()

    rendered = "\n".join(JsonFormatter().format(r) for r in records)

    assert SUGGESTION_TEXT not in rendered
    assert CHUNK_TEXT not in rendered
    assert IDEA_PHRASE not in rendered  # idea text substring


async def test_suppression_is_logged_with_reason_not_text():
    _session, records = await _run_capturing()
    by_msg = {r.getMessage(): r for r in records}

    # The rate-limited 4th idea logs a pacing decision with a reason, no text.
    assert "pacing_decision" in by_msg
    decisions = [r for r in records if r.getMessage() == "pacing_decision"]
    assert any(getattr(r, "delivered") is False and getattr(r, "reason") == "RateLimited" for r in decisions)
