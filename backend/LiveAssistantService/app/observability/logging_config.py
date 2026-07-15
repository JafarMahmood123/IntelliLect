"""Structured JSON logging with per-session correlation ids.

Callers pass only counts / ids / durations / types / reasons as structured extras —
NEVER transcript text, idea text, suggestion text, chunk text, secrets, or audio
(hard privacy rule: course content + teacher-private feedback).
"""

from __future__ import annotations

import json
import logging
from datetime import datetime, timezone

from app.observability.correlation import run_id_var, session_id_var

# Standard LogRecord attributes; everything else on a record is a structured "extra".
_STANDARD_ATTRS = frozenset(
    {
        "name", "msg", "args", "levelname", "levelno", "pathname", "filename",
        "module", "exc_info", "exc_text", "stack_info", "lineno", "funcName",
        "created", "msecs", "relativeCreated", "thread", "threadName",
        "processName", "process", "taskName", "message", "asctime",
        "color_message", "session_id", "run_id",
    }
)


class CorrelationFilter(logging.Filter):
    """Attach the active session_id / run_id (if any) to every record."""

    def filter(self, record: logging.LogRecord) -> bool:
        record.session_id = session_id_var.get()
        record.run_id = run_id_var.get()
        return True


class JsonFormatter(logging.Formatter):
    """Compact JSON lines with correlation ids and any structured `extra` fields."""

    def format(self, record: logging.LogRecord) -> str:
        payload: dict[str, object] = {
            "ts": datetime.fromtimestamp(record.created, tz=timezone.utc).isoformat(),
            "level": record.levelname,
            "logger": record.name,
            "message": record.getMessage(),
        }
        session_id = getattr(record, "session_id", None)
        run_id = getattr(record, "run_id", None)
        if session_id:
            payload["session_id"] = session_id
        if run_id:
            payload["run_id"] = run_id
        for key, value in record.__dict__.items():
            if key not in _STANDARD_ATTRS and not key.startswith("_"):
                payload[key] = value
        if record.exc_info:
            payload["exc"] = self.formatException(record.exc_info)
        return json.dumps(payload, default=str)


def configure_logging(level: str = "INFO") -> None:
    """Install the JSON formatter + correlation filter on the root logger.

    Idempotent: replaces our handler on repeated calls (e.g. across app factories in
    tests) so log lines are not duplicated.
    """
    root = logging.getLogger()
    root.setLevel(level.upper())
    for handler in list(root.handlers):
        root.removeHandler(handler)
    handler = logging.StreamHandler()
    handler.setFormatter(JsonFormatter())
    handler.addFilter(CorrelationFilter())
    root.addHandler(handler)
