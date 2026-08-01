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

from collections.abc import AsyncIterator

from app.application.ports.audio_source import AudioSource
from app.application.ports.brain_client import BrainClient
from app.application.ports.speech_to_text import SpeechToText
from app.application.services.boundary_detector import BoundaryDetector
from app.application.services.feedback_dispatcher import FeedbackDispatcher
from app.application.services.feedback_pacer import FeedbackPacer
from app.application.services.idea_evaluator import IdeaEvaluator
from app.application.services.last_idea_store import LastIdeaStore
from app.application.services.stream_prefetch import prefetch
from app.application.services.token_estimate import estimate_tokens
from app.application.services.transcript_recorder import TranscriptRecorder
from app.domain.entities.session_context import SessionContext
from app.domain.evaluation.evaluation_outcome import EvaluationOutcome
from app.domain.evaluation.feedback_type import FeedbackType
from app.domain.evaluation.teacher_suggestion import TeacherSuggestion
from app.domain.idea.completed_idea import CompletedIdea
from app.domain.transcript.transcript_segment import TranscriptSegment
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
        transcript_recorder: TranscriptRecorder | None = None,
        smoke_brain: BrainClient | None = None,
        last_ideas: LastIdeaStore | None = None,
        prefetch_segments: int = 4,
    ) -> None:
        self._session = session
        self._audio_source = audio_source
        self._stt = speech_to_text
        self._boundary = boundary_detector
        self._evaluator = idea_evaluator
        self._pacer = feedback_pacer
        self._dispatcher = feedback_dispatcher
        # SMOKE TEST (temporary): when set (ASSISTANT_SMOKE_TEST=true), each completed idea's raw
        # transcript is sent straight to this brain and the reply is delivered to the teacher,
        # bypassing retrieval/evaluation/pacing. None => the normal (real) pipeline runs.
        self._smoke_brain = smoke_brain
        # Optional (S-0): when present, FINAL segments are persisted incrementally,
        # off the feedback hot path. Absent -> no persistence (unchanged LA-6 behavior).
        self._recorder = transcript_recorder
        # Optional: when present, every finished idea is teed in here so the quiz generator can
        # ask what the teacher was just explaining. Read-only from this pipeline's perspective —
        # it never changes what the evaluator sees.
        self._last_ideas = last_ideas
        # How many finalized segments may sit transcribed-but-not-yet-embedded. Small on
        # purpose: this is a jitter absorber, not a lecture buffer, and a full queue simply
        # applies backpressure to the STT.
        self._prefetch_segments = prefetch_segments
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
                # Make the transcript durable from the very start of the session (S-0).
                if self._recorder is not None:
                    await self._recorder.start()
                await self._audio_source.connect(self._session)
                logger.info("agent_joined")
                # frames -> transcript segments -> completed ideas (each stage is lazy).
                # The persist tee is a pass-through: it never changes the segment stream
                # the boundary detector sees, it only records FINAL segments alongside.
                # prefetch decouples STT from the boundary detector's per-segment embedding
                # round trip. Without it the two stages alternate — nothing is transcribed
                # while an embedding is in flight — and the serial cost per window is
                # STT + embed. See stream_prefetch for the measurements.
                segments = self._persist_final(
                    prefetch(
                        self._stt.transcribe(self._audio_source.frames()),
                        self._prefetch_segments,
                    )
                )
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
                # Drain + mark the transcript Finalized on any end (S-0). Non-fatal:
                # finalize swallows its own store errors; suppress guards teardown too.
                if self._recorder is not None:
                    with contextlib.suppress(Exception):
                        await self._recorder.finalize()
                # Release this session's pacing state (LA-7) on any end.
                self._pacer.reset(session_id)
                # Same for its retained ideas — the lecture they belong to is over.
                if self._last_ideas is not None:
                    self._last_ideas.release(session_id)
                metrics.active_sessions_dec()
                logger.info("session_ended")

    async def _persist_final(
        self, segments: AsyncIterator[TranscriptSegment]
    ) -> AsyncIterator[TranscriptSegment]:
        """Pass segments through unchanged, recording FINAL ones off the hot path (S-0).

        ``record`` only enqueues (never awaits the store, never raises), so the boundary
        detector — and thus the feedback loop — is never slowed or broken by persistence.
        Interim segments are yielded onward untouched but never recorded.
        """
        async for segment in segments:
            if self._recorder is not None and segment.is_final:
                self._recorder.record(segment)
            yield segment

    async def _handle_idea(self, idea: CompletedIdea) -> None:
        """Evaluate one idea, PACE it (LA-7), then deliver; never let one idea stop the run."""
        # Retained BEFORE anything can filter it. What the teacher just explained does not depend
        # on whether the assistant had feedback about it, so this sits ahead of the smoke branch,
        # the evaluator and the pacer alike.
        if self._last_ideas is not None:
            self._last_ideas.record(self._session.session_id, idea)

        # SMOKE TEST (temporary): with a smoke brain injected, bypass retrieval/evaluation/pacing
        # and push the LLM's raw reply to the teacher. Restore the real path by flipping
        # ASSISTANT_SMOKE_TEST off (so smoke_brain is None) — no other change needed.
        if self._smoke_brain is not None:
            await self._handle_idea_smoke(idea)
            return
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

    async def _handle_idea_smoke(self, idea: CompletedIdea) -> None:
        """SMOKE TEST ONLY (temporary): raw transcript idea -> LLM -> teacher.

        Bypasses retrieval, grounded evaluation, and pacing to prove the transcript -> LLM ->
        teacher path end to end. Never lets one idea stop the run. Remove with the smoke branch
        in _handle_idea once the real assistant is verified.
        """
        try:
            logger.info("smoke_idea", extra={"tokens": estimate_tokens(idea.text)})
            reply = (await self._smoke_brain.smoke_complete(idea.text) or "").strip()
            if not reply:
                logger.warning("smoke_llm_empty")
                return
            suggestion = TeacherSuggestion(text=reply, type=FeedbackType.UNCLEAR)
            outcome = EvaluationOutcome(has_feedback=True, suggestion=suggestion)
            await self._dispatcher.dispatch(outcome, self._session)
            logger.info("smoke_feedback_delivered")
        except asyncio.CancelledError:
            raise
        except Exception as exc:  # noqa: BLE001 — one bad idea must not end the session
            logger.error("smoke_idea_failed", extra={"error_type": type(exc).__name__})
