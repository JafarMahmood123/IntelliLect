from __future__ import annotations

import contextvars
from collections.abc import Iterator
from contextlib import contextmanager
from uuid import UUID, uuid4

# Per-task correlation ids. Set once at the start of a document's ingestion so every
# log line emitted while processing it (across nested awaits) carries the same ids.
file_id_var: contextvars.ContextVar[str | None] = contextvars.ContextVar(
    "file_id", default=None
)
run_id_var: contextvars.ContextVar[str | None] = contextvars.ContextVar(
    "run_id", default=None
)


@contextmanager
def correlation_scope(
    file_id: UUID | str, run_id: UUID | str | None = None
) -> Iterator[str]:
    """Bind file_id (+ a generated run/trace id) for the duration of the block."""
    resolved_run_id = str(run_id) if run_id is not None else str(uuid4())
    file_token = file_id_var.set(str(file_id))
    run_token = run_id_var.set(resolved_run_id)
    try:
        yield resolved_run_id
    finally:
        file_id_var.reset(file_token)
        run_id_var.reset(run_token)


def current_correlation() -> dict[str, str]:
    """The active correlation ids, if any (for attaching to non-log contexts)."""
    ids: dict[str, str] = {}
    file_id = file_id_var.get()
    run_id = run_id_var.get()
    if file_id:
        ids["file_id"] = file_id
    if run_id:
        ids["run_id"] = run_id
    return ids
