"""Small polling helpers for the eventually-consistent parts of the flow."""

from __future__ import annotations

import time
from collections.abc import Callable
from typing import TypeVar

T = TypeVar("T")


class WaitTimeout(AssertionError):
    """Raised when a condition did not become true within the deadline."""


def poll_until(
    predicate: Callable[[], T | None],
    *,
    timeout_s: float,
    interval_s: float = 1.0,
    description: str = "condition",
) -> T:
    """Call ``predicate`` until it returns a truthy value or the deadline passes.

    Returns the truthy value. Raises ``WaitTimeout`` on expiry. Exceptions from the
    predicate are treated as "not ready yet" and retried (the last one is reported).
    """
    deadline = time.monotonic() + timeout_s
    last_error: Exception | None = None
    while time.monotonic() < deadline:
        try:
            value = predicate()
        except Exception as exc:  # noqa: BLE001 — transient during startup
            last_error = exc
            value = None
        if value:
            return value
        time.sleep(interval_s)
    suffix = f" (last error: {last_error!r})" if last_error else ""
    raise WaitTimeout(f"Timed out after {timeout_s:.0f}s waiting for {description}.{suffix}")
