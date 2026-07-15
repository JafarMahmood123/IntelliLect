"""The full live loop for ONE session, assembled from the phase components (LA-6).

Chains, for a given ``SessionContext``:
    AudioSource (LA-1) -> SpeechToText (LA-2) -> BoundaryDetector (LA-3)
    -> IdeaEvaluator (LA-4) -> (has_feedback) -> FeedbackSink (LA-5, via FeedbackDispatcher)

Runs as one async task that consumes the audio stream to completion. Per-idea errors
are caught and logged so one bad idea never stops the session; the trailing idea is
flushed by the boundary detector on stream end. Stopping cancels the task and
disconnects the ``AudioSource`` cleanly.

Pure application logic: depends only on ports/services + domain — no infrastructure,
no framework. LA-7 (pacing/rate-limiting/dedup) will slot between the evaluator and
the dispatcher without changing this shape.
"""

from __future__ import annotations

import asyncio
import contextlib
import logging

from app.application.ports.audio_source import AudioSource
from app.application.ports.speech_to_text import SpeechToText
from app.application.services.boundary_detector import BoundaryDetector
from app.application.services.feedback_dispatcher import FeedbackDispatcher
from app.application.services.feedback_pacer import FeedbackPacer
from app.application.services.idea_evaluator import IdeaEvaluator
from app.domain.entities.session_context import SessionContext

logger = logging.getLogger("liveassistant.pipeline")


class SessionPipeline:
    """Owns the async task that runs audio → STT → ideas → evaluate → pace → feedback."""

    def __init__(
        self,
        session: SessionContext,
        audio_source: AudioSource,
        speech_to_text: SpeechToText,
        boundary_detector: BoundaryDetector,
        idea_evaluator: IdeaEvaluator,
        feedback_pacer: FeedbackPacer,
        feedback_dispatcher: FeedbackDispatcher,
    ) -> None:
        self._session = session
        self._audio_source = audio_source
        self._stt = speech_to_text
        self._boundary = boundary_detector
        self._evaluator = idea_evaluator
        self._pacer = feedback_pacer
        self._dispatcher = feedback_dispatcher
        self._task: asyncio.Task | None = None

    def start(self) -> asyncio.Task:
        """Launch the pipeline as a background task and return it. Idempotent."""
        if self._task is None:
            self._task = asyncio.create_task(
                self._run(), name=f"session-pipeline-{self._session.session_id}"
            )
        return self._task

    async def stop(self) -> None:
        """Cancel the task and disconnect the source. Idempotent and safe to await."""
        task = self._task
        if task is None:
            # Never started — still release any partially-acquired source.
            with contextlib.suppress(Exception):
                await self._audio_source.disconnect()
            return
        task.cancel()
        with contextlib.suppress(asyncio.CancelledError):
            await task

    async def _run(self) -> None:
        session_id = self._session.session_id
        logger.info("Session pipeline starting for %s", session_id)
        try:
            await self._audio_source.connect(self._session)
            # frames -> transcript segments -> completed ideas (each stage is lazy).
            segments = self._stt.transcribe(self._audio_source.frames())
            async for idea in self._boundary.process(segments):
                await self._handle_idea(idea)
        except asyncio.CancelledError:
            logger.info("Session pipeline cancelled for %s", session_id)
            raise
        except Exception:  # noqa: BLE001 — the loop must not propagate arbitrary faults
            logger.exception("Session pipeline crashed for %s", session_id)
        finally:
            with contextlib.suppress(Exception):
                await self._audio_source.disconnect()
            # Release this session's pacing state (LA-7) on any end: stop, crash, or
            # natural stream end all run this finally.
            self._pacer.reset(session_id)
            logger.info("Session pipeline stopped for %s", session_id)

    async def _handle_idea(self, idea) -> None:
        """Evaluate one idea, PACE it (LA-7), then deliver; never let one idea stop the run."""
        try:
            outcome = await self._evaluator.evaluate(idea, self._session)
            if not outcome.has_feedback or outcome.suggestion is None:
                return  # nothing to deliver
            # Gate through the pacer BEFORE delivery: rate-limit / low-confidence / dedup.
            decision = self._pacer.decide(self._session.session_id, outcome.suggestion)
            if decision.deliver:
                await self._dispatcher.dispatch(outcome, self._session)
            else:
                # Metrics-friendly: never log suggestion text — type/reason/confidence only.
                logger.info(
                    "Suppressed feedback in session %s: reason=%s type=%s confidence=%.2f",
                    self._session.session_id,
                    decision.reason.value,
                    outcome.suggestion.type.value,
                    outcome.suggestion.confidence,
                )
        except asyncio.CancelledError:
            raise
        except Exception:  # noqa: BLE001 — one bad idea must not end the session
            logger.exception("Failed to process an idea in session %s", self._session.session_id)
