"""Per-session log correlation via contextvars.

``session_scope`` binds the ``session_id`` (and a generated ``run_id``) for the
duration of a session pipeline, so every log line emitted while processing it — across
nested awaits within the task — carries the same ids. Threaded through
``SessionPipeline`` (LA-8).
"""

from __future__ import annotations

import contextvars
from collections.abc import Iterator
from contextlib import contextmanager
from uuid import UUID, uuid4

session_id_var: contextvars.ContextVar[str | None] = contextvars.ContextVar(
    "session_id", default=None
)
run_id_var: contextvars.ContextVar[str | None] = contextvars.ContextVar(
    "run_id", default=None
)


@contextmanager
def session_scope(
    session_id: UUID | str, run_id: UUID | str | None = None
) -> Iterator[str]:
    """Bind session_id (+ a generated run/trace id) for the duration of the block."""
    resolved_run_id = str(run_id) if run_id is not None else str(uuid4())
    session_token = session_id_var.set(str(session_id))
    run_token = run_id_var.set(resolved_run_id)
    try:
        yield resolved_run_id
    finally:
        session_id_var.reset(session_token)
        run_id_var.reset(run_token)


def current_correlation() -> dict[str, str]:
    """The active correlation ids, if any."""
    ids: dict[str, str] = {}
    session_id = session_id_var.get()
    run_id = run_id_var.get()
    if session_id:
        ids["session_id"] = session_id
    if run_id:
        ids["run_id"] = run_id
    return ids
