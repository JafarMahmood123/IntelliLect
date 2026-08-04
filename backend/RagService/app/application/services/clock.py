from __future__ import annotations

import asyncio
from datetime import datetime, timezone
from typing import Protocol, runtime_checkable


@runtime_checkable
class Clock(Protocol):
    """Time + sleep abstraction so retry backoff and stale detection are testable.

    Production uses SystemClock; tests inject a FakeClock to control `now()` and to
    assert backoff timing without real sleeps.
    """

    def now(self) -> datetime:
        ...

    async def sleep(self, seconds: float) -> None:
        ...


class SystemClock:
    """Real wall clock (UTC) and real asyncio sleep."""

    def now(self) -> datetime:
        return datetime.now(timezone.utc)

    async def sleep(self, seconds: float) -> None:
        await asyncio.sleep(seconds)
