"""Unique identifiers so re-running the suite never collides on email/username."""

from __future__ import annotations

import os
import time


def _suffix() -> str:
    # Monotonic-ish + pid keeps values unique across parallel runs without needing
    # a random source (and stays readable in the DB while debugging).
    return f"{int(time.time())}{os.getpid() % 1000:03d}"


def unique_email(role: str) -> str:
    return f"e2e-{role}-{_suffix()}@example.test"


def unique_username(role: str) -> str:
    return f"e2e_{role}_{_suffix()}"
