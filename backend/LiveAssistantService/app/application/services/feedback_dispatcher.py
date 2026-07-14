"""The thin connector from an evaluation outcome to feedback delivery (LA-5).

An ``EvaluationOutcome`` with ``has_feedback=True`` is sent to the teacher via the
``FeedbackSink``; an outcome with no feedback is dropped (the common case — no send).
A delivery failure is logged and swallowed so a failed send never breaks the live
loop. Pure application logic: depends only on the ``FeedbackSink`` port + domain.

Injectable and NOT connected to a live session yet (that is LA-6).
"""

from __future__ import annotations

import logging
from collections.abc import AsyncIterator

from app.application.ports.feedback_sink import FeedbackSink
from app.domain.entities.session_context import SessionContext
from app.domain.evaluation.evaluation_outcome import EvaluationOutcome

logger = logging.getLogger("liveassistant.feedback")


class FeedbackDispatcher:
    """Routes evaluation outcomes to the teacher-only ``FeedbackSink``."""

    def __init__(self, sink: FeedbackSink) -> None:
        self._sink = sink

    async def dispatch(
        self, outcome: EvaluationOutcome, session: SessionContext
    ) -> bool:
        """Deliver the suggestion if the outcome has one. Returns whether it was sent.

        No-feedback outcomes are dropped without a send. A ``FeedbackSink`` error is
        logged and swallowed (returns False) — delivery must never break the loop.
        """
        if not outcome.has_feedback or outcome.suggestion is None:
            return False
        try:
            await self._sink.send(session, outcome.suggestion)
            return True
        except Exception:  # noqa: BLE001 — a failed send must not break the loop
            logger.exception("Feedback delivery failed; continuing.")
            return False

    async def process(
        self, outcomes: AsyncIterator[EvaluationOutcome], session: SessionContext
    ) -> AsyncIterator[bool]:
        """Dispatch a stream of outcomes, yielding whether each was delivered."""
        async for outcome in outcomes:
            yield await self.dispatch(outcome, session)
