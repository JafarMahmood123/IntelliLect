"""Deliver a TeacherSuggestion through the LA-5 connector + sink and show exactly
what would go on the wire, and TO WHOM.

Default mode is fully OFFLINE — NO LiveKit. It runs a suggestion through the real
FeedbackDispatcher + LiveKitFeedbackSink wired to an in-process recording channel, so
the captured bytes are the actual serialized payload and the captured identity is the
real resolved target (the teacher, never a student). It also shows that a
no-feedback outcome is dropped (no send):

    python scripts/feedback_check.py

``--live`` publishes to a REAL room via the agent's connection. DEFERRED: needs a live
session with the teacher present:

    python scripts/feedback_check.py --live --room <room> --teacher <teacher_identity>
"""

from __future__ import annotations

import argparse
import asyncio
import json
import sys
from pathlib import Path
from uuid import uuid4

# Allow running directly from source without an editable install.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from app.api.dependencies import build_feedback_dispatcher, build_feedback_sink
from app.application.ports.agent_data_channel import AgentDataChannel
from app.domain.entities.session_context import SessionContext
from app.domain.evaluation.evaluation_outcome import EvaluationOutcome
from app.domain.evaluation.feedback_type import FeedbackType
from app.domain.evaluation.retrieved_chunk import RetrievedChunk
from app.domain.evaluation.teacher_suggestion import TeacherSuggestion
from app.infrastructure.config.settings import get_settings


class _RecordingChannel(AgentDataChannel):
    """In-process AgentDataChannel that captures publishes instead of using LiveKit."""

    def __init__(self) -> None:
        self.publishes: list[tuple[str, bytes, str]] = []

    async def publish_to_identity(self, identity, payload, *, topic=""):  # noqa: D102
        self.publishes.append((identity, payload, topic))


def _sample_outcome() -> EvaluationOutcome:
    sources = [
        RetrievedChunk("chloroplast, not mitochondria", 0.82, uuid4(), uuid4(), slide=4),
        RetrievedChunk("light-dependent reactions", 0.66, uuid4(), uuid4(), page=12),
    ]
    return EvaluationOutcome(
        has_feedback=True,
        suggestion=TeacherSuggestion(
            text="Photosynthesis happens in the chloroplast, not the mitochondria [1]; "
            "consider clarifying the light-dependent reactions [2].",
            type=FeedbackType.DISCREPANCY,
            citations=[1, 2],
            sources=sources,
        ),
    )


async def _run_offline() -> int:
    settings = get_settings()
    session = SessionContext(uuid4(), uuid4(), teacher_identity="teacher-1", room_name="demo")
    channel = _RecordingChannel()
    dispatcher = build_feedback_dispatcher(build_feedback_sink(settings, channel))

    print("[offline] EvaluationOutcome -> FeedbackDispatcher -> LiveKitFeedbackSink "
          "(recording channel, NO LiveKit)")
    print(f"[offline] transport={settings.feedback_transport} version={settings.feedback_message_version}")
    print("-" * 72)

    # 1) A no-feedback outcome is dropped (no publish).
    dropped = await dispatcher.dispatch(EvaluationOutcome.none(), session)
    print(f"no-feedback outcome -> sent={dropped} (dropped, nothing published)")

    # 2) A real suggestion is delivered to the teacher ONLY.
    sent = await dispatcher.dispatch(_sample_outcome(), session)
    print(f"suggestion outcome  -> sent={sent}")
    print("-" * 72)

    assert len(channel.publishes) == 1, "expected exactly one publish"
    identity, payload, topic = channel.publishes[0]
    print(f"target identity : {identity}   (session.teacher_identity)")
    print(f"topic           : {topic}")
    print("payload (the exact bytes published to the teacher):")
    print(json.dumps(json.loads(payload), indent=2))

    ok = identity == session.teacher_identity
    print("-" * 72)
    print("RESULT          :", "OK (teacher-only)" if ok else "WRONG TARGET")
    return 0 if ok else 2


async def _run_live(room: str, teacher: str) -> int:
    from app.infrastructure.audio.livekit_audio_source import LiveKitAudioSource

    settings = get_settings()
    session = SessionContext(uuid4(), uuid4(), teacher_identity=teacher, room_name=room)
    agent = LiveKitAudioSource(settings)  # also an AgentDataChannel
    print(f"[live] joining room={room!r}; delivering feedback to teacher={teacher!r} only")
    await agent.connect(session)
    try:
        dispatcher = build_feedback_dispatcher(build_feedback_sink(settings, agent))
        sent = await dispatcher.dispatch(_sample_outcome(), session)
        print(f"[live] delivered={sent}")
    finally:
        await agent.disconnect()
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Teacher-only feedback delivery check (LA-5).")
    parser.add_argument("--live", action="store_true", help="DEFERRED: publish to a real room.")
    parser.add_argument("--room", help="LiveKit room name (--live).")
    parser.add_argument("--teacher", help="Teacher participant identity (--live).")
    args = parser.parse_args(argv)

    if args.live:
        if not args.room or not args.teacher:
            print("--live requires --room and --teacher.", file=sys.stderr)
            return 1
        return asyncio.run(_run_live(args.room, args.teacher))
    return asyncio.run(_run_offline())


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
