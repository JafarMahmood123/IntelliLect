from __future__ import annotations

import json
import logging
from datetime import datetime, timezone

from app.observability.correlation import file_id_var, run_id_var

# Standard LogRecord attributes; everything else on a record is treated as a
# structured "extra" field and included in the JSON payload.
_STANDARD_ATTRS = frozenset(
    {
        "name", "msg", "args", "levelname", "levelno", "pathname", "filename",
        "module", "exc_info", "exc_text", "stack_info", "lineno", "funcName",
        "created", "msecs", "relativeCreated", "thread", "threadName",
        "processName", "process", "taskName", "message", "asctime",
        "color_message", "file_id", "run_id",
    }
)


class CorrelationFilter(logging.Filter):
    """Attach the active file_id / run_id (if any) to every record."""

    def filter(self, record: logging.LogRecord) -> bool:
        record.file_id = file_id_var.get()
        record.run_id = run_id_var.get()
        return True


class JsonFormatter(logging.Formatter):
    """Compact JSON lines with correlation ids and any structured `extra` fields.

    Only counts/ids/sizes/durations are ever passed as extras by callers — never
    file contents, chunk text, secrets, or auth headers.
    """

    def format(self, record: logging.LogRecord) -> str:
        payload: dict[str, object] = {
            "ts": datetime.fromtimestamp(record.created, tz=timezone.utc).isoformat(),
            "level": record.levelname,
            "logger": record.name,
            "message": record.getMessage(),
        }
        file_id = getattr(record, "file_id", None)
        run_id = getattr(record, "run_id", None)
        if file_id:
            payload["file_id"] = file_id
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

    Idempotent: replaces our handler on repeated calls (e.g. across app factories
    in tests) so log lines are not duplicated.
    """
    root = logging.getLogger()
    root.setLevel(level.upper())
    for handler in list(root.handlers):
        root.removeHandler(handler)
    handler = logging.StreamHandler()
    handler.setFormatter(JsonFormatter())
    handler.addFilter(CorrelationFilter())
    root.addHandler(handler)
