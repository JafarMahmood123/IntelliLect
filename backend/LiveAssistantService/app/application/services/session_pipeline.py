"""The full live loop for ONE session, assembled from the phase components (LA-6).

Chains, for a given ``SessionContext``:
    AudioSource (LA-1) -> SpeechToText (LA-2) -> BoundaryDetector (LA-3)
    -> IdeaEvaluator (LA-4) -> pacing (LA-7) -> FeedbackSink (LA-5, via FeedbackDispatcher)

Runs as one async task that consumes the audio stream to completion. Per-idea errors
are caught and logged so one bad idea never stops the session; the trailing idea is
flushed by the boundary detector on stream end. Stopping cancels the task and
disconnects the ``AudioSource`` cleanly.

LA-8 instrumentation is ADDITIVE and does not change behavior: a ``session_scope``
binds session_id/run_id to every log line, lifecycle events log at INFO with
structured extras (counts/ids/types/durations only — NEVER transcript/idea/suggestion
text), and stage/latency metrics wrap the existing calls.

Pure application logic: depends only on ports/services + domain + observability.
"""

from __future__ import annotations

import asyncio
import contextlib
import logging
import time

from app.application.ports.audio_source import AudioSource
from app.application.ports.speech_to_text import SpeechToText
from app.application.services.boundary_detector import BoundaryDetector
from app.application.services.feedback_dispatcher import FeedbackDispatcher
from app.application.services.feedback_pacer import FeedbackPacer
from app.application.services.idea_evaluator import IdeaEvaluator
from app.application.services.token_estimate import estimate_tokens
from app.domain.entities.session_context import SessionContext
from app.domain.idea.completed_idea import CompletedIdea
from app.observability import metrics
from app.observability.correlation import session_scope

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
        with session_scope(session_id):
            metrics.record_session_started()
            metrics.active_sessions_inc()
            logger.info("session_started")
            try:
                await self._audio_source.connect(self._session)
                logger.info("agent_joined")
                # frames -> transcript segments -> completed ideas (each stage is lazy).
                segments = self._stt.transcribe(self._audio_source.frames())
                last = time.perf_counter()
                async for idea in self._boundary.process(segments):
                    # Time to detect/emit this idea (audio pull + STT + boundary).
                    metrics.observe_stage("boundary", time.perf_counter() - last)
                    await self._handle_idea(idea)
                    last = time.perf_counter()
            except asyncio.CancelledError:
                logger.info("session_cancelled")
                raise
            except Exception as exc:  # noqa: BLE001 — the loop must not propagate faults
                # Log the error TYPE only — never a raw message/traceback that could
                # carry course content.
                logger.error("session_crashed", extra={"error_type": type(exc).__name__})
            finally:
                with contextlib.suppress(Exception):
                    await self._audio_source.disconnect()
                logger.info("agent_left")
                # Release this session's pacing state (LA-7) on any end.
                self._pacer.reset(session_id)
                metrics.active_sessions_dec()
                logger.info("session_ended")

    async def _handle_idea(self, idea: CompletedIdea) -> None:
        """Evaluate one idea, PACE it (LA-7), then deliver; never let one idea stop the run."""
        try:
            received_at = time.perf_counter()
            metrics.record_idea(idea.trigger.value)
            logger.info(
                "idea_completed",
                extra={
                    "trigger": idea.trigger.value,
                    "tokens": estimate_tokens(idea.text),
                    "segments": idea.segment_count,
                    "duration_ms": idea.duration_ms,
                },
            )

            outcome = await self._evaluator.evaluate(idea, self._session)
            metrics.record_evaluation(outcome.has_feedback)
            feedback_type = outcome.suggestion.type.value if outcome.suggestion else None
            logger.info(
                "evaluation", extra={"has_feedback": outcome.has_feedback, "type": feedback_type}
            )
            if not outcome.has_feedback or outcome.suggestion is None:
                return  # nothing to deliver

            # Gate through the pacer BEFORE delivery: rate-limit / low-confidence / dedup.
            decision = self._pacer.decide(self._session.session_id, outcome.suggestion)
            logger.info(
                "pacing_decision",
                extra={
                    "delivered": decision.deliver,
                    "reason": decision.reason.value,
                    "type": outcome.suggestion.type.value,
                    "confidence": round(outcome.suggestion.confidence, 2),
                },
            )
            if not decision.deliver:
                metrics.record_suppressed(decision.reason.value)
                return

            with metrics.stage_timer("delivery"):
                await self._dispatcher.dispatch(outcome, self._session)
            metrics.record_delivered(outcome.suggestion.type.value)
            metrics.observe_idea_to_feedback(time.perf_counter() - received_at)
            logger.info("feedback_delivered", extra={"type": outcome.suggestion.type.value})
        except asyncio.CancelledError:
            raise
        except Exception as exc:  # noqa: BLE001 — one bad idea must not end the session
            logger.error("idea_failed", extra={"error_type": type(exc).__name__})
