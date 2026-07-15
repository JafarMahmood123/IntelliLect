"""End-to-end: the REAL SessionManager + SessionPipeline over the whole live loop,
wired to fakes only (LA-8). No STT model, no Ollama, no LiveKit.
"""

from __future__ import annotations

import asyncio
from uuid import uuid4

from app.application.services.session_manager import SessionManager
from app.domain.entities.session_context import SessionContext
from tests.support.fake_feedback_sink import FakeFeedbackSink
from tests.support.pipeline_harness import ScriptedBrainClient, build_scenario_pipeline


async def test_full_loop_segments_evaluates_paces_and_delivers_teacher_only():
    sink, brain = FakeFeedbackSink(), ScriptedBrainClient()
    created = []

    def factory(session: SessionContext):
        pipeline = build_scenario_pipeline(session, sink=sink, brain=brain)
        created.append(pipeline)
        return pipeline

    manager = SessionManager(factory, max_concurrent=5)
    session = SessionContext(uuid4(), uuid4(), "teacher-9", "room-9")

    pipeline = await manager.start(session)
    assert manager.active_count() == 1          # exactly one pipeline launched
    assert len(created) == 1

    await pipeline.start()                       # idempotent -> awaits the running task
    await asyncio.sleep(0)                        # let the done-callback deregister
    assert manager.active_count() == 0           # torn down after completion

    # Four ideas were segmented and evaluated (alpha/beta/gamma/delta):
    #   alpha "wrong" -> feedback -> delivered
    #   beta  "correct" -> no feedback
    #   gamma "boom" -> brain raises -> caught -> no feedback (session survives)
    #   delta "wrong" -> feedback -> rate-limited (flushed trailing idea still processed)
    assert brain.calls == 4

    # Only the first flagged idea reached the teacher — and ONLY the teacher.
    assert len(sink.calls) == 1
    assert sink.target_identities == ["teacher-9"]


async def test_start_then_stop_tears_down_exactly_one_pipeline():
    sink, brain = FakeFeedbackSink(), ScriptedBrainClient()
    manager = SessionManager(
        lambda s: build_scenario_pipeline(s, sink=sink, brain=brain), max_concurrent=5
    )
    session = SessionContext(uuid4(), uuid4(), "teacher-1", "room-1")

    await manager.start(session)
    assert manager.active_count() == 1

    assert await manager.stop(session.session_id) is True
    assert manager.active_count() == 0
