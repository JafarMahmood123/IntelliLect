"""Evaluate a CompletedIdea against course material with IdeaEvaluator (LA-4) and
print the EvaluationOutcome.

Default mode is fully OFFLINE — NO RagService, NO Ollama. It runs a scripted
idea through a fake retrieval client (fixture chunks) and a fake brain (deterministic
outcome), proving the orchestration + citation→source mapping:

    python scripts/evaluate_check.py

``--live`` runs the REAL clients — RagService retrieval + the Ollama brain —
against a classroom and an idea string. DEFERRED: needs RagService reachable at
RAG_BASE_URL and Ollama running with EVAL_MODEL pulled:

    python scripts/evaluate_check.py --live --classroom <uuid> --idea "the teacher's explanation"
"""

from __future__ import annotations

import argparse
import asyncio
import sys
from pathlib import Path
from uuid import UUID, uuid4

# Allow running directly from source without an editable install.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from app.api.dependencies import build_idea_evaluator
from app.application.ports.brain_client import BrainClient
from app.application.ports.retrieval_client import RetrievalClient
from app.domain.entities.session_context import SessionContext
from app.domain.evaluation.evaluation_outcome import EvaluationOutcome
from app.domain.evaluation.feedback_type import FeedbackType
from app.domain.evaluation.retrieved_chunk import RetrievedChunk
from app.domain.evaluation.teacher_suggestion import TeacherSuggestion
from app.domain.idea.boundary_trigger import BoundaryTrigger
from app.domain.idea.completed_idea import CompletedIdea
from app.infrastructure.config.settings import get_settings


# --- Offline demo fakes (self-contained so this script ships without tests/) -------
class _FixtureRetrievalClient(RetrievalClient):
    def __init__(self, chunks: list[RetrievedChunk]) -> None:
        self._chunks = chunks

    async def retrieve(self, classroom_id, query_text, top_k):  # noqa: D102
        return list(self._chunks)


class _FixtureBrainClient(BrainClient):
    """Cites the two provided chunks as a discrepancy — deterministic, no model."""

    async def evaluate(self, idea, chunks):  # noqa: D102
        cited = chunks[:2]
        return EvaluationOutcome(
            has_feedback=True,
            suggestion=TeacherSuggestion(
                text="The explanation conflicts with the material on photosynthesis "
                "location [1]; also clarify the light-dependent reactions [2].",
                type=FeedbackType.DISCREPANCY,
                citations=[1, 2][: len(cited)],
                sources=cited,
            ),
        )


def _fixture_chunks() -> list[RetrievedChunk]:
    return [
        RetrievedChunk(
            "Photosynthesis occurs in the chloroplast, not the mitochondria.",
            score=0.82, chunk_id=uuid4(), document_id=uuid4(), slide=4,
        ),
        RetrievedChunk(
            "The light-dependent reactions take place in the thylakoid membrane.",
            score=0.66, chunk_id=uuid4(), document_id=uuid4(), page=12,
        ),
        RetrievedChunk(
            "Low-relevance chunk that the demo brain does not cite.",
            score=0.30, chunk_id=uuid4(), document_id=uuid4(),
        ),
    ]


def _print_outcome(outcome: EvaluationOutcome) -> None:
    print(f"has_feedback : {outcome.has_feedback}")
    if not outcome.suggestion:
        print("(no suggestion — biased to silence)")
        return
    suggestion = outcome.suggestion
    print(f"type         : {suggestion.type.value}")
    print(f"citations    : {suggestion.citations}")
    print(f"suggestion   : {suggestion.text}")
    print("sources:")
    for number, chunk in zip(suggestion.citations, suggestion.sources):
        loc = (
            f"slide {chunk.slide}" if chunk.slide is not None
            else f"page {chunk.page}" if chunk.page is not None
            else f"section {chunk.section}" if chunk.section else "—"
        )
        print(f"  [{number}] ({loc}) score={chunk.score:.2f}  {chunk.text}")


async def _run_offline() -> int:
    settings = get_settings()
    idea = CompletedIdea(
        text="Photosynthesis happens in the mitochondria of the plant cell.",
        start_ms=0, end_ms=8000, segment_count=3, trigger=BoundaryTrigger.PAUSE,
    )
    session = SessionContext(uuid4(), uuid4(), "teacher-1", "demo-room")
    evaluator = build_idea_evaluator(
        settings, _FixtureRetrievalClient(_fixture_chunks()), _FixtureBrainClient()
    )

    print("[offline] scripted idea -> IdeaEvaluator (NO RagService, NO Ollama)")
    print(f"[offline] top_k={settings.retrieval_top_k} min_score={settings.retrieval_min_score}")
    print(f"idea: {idea.text}")
    print("-" * 72)
    _print_outcome(await evaluator.evaluate(idea, session))
    return 0


async def _run_live(classroom_id: str, idea_text: str) -> int:
    from app.api.dependencies import build_brain_client, build_retrieval_client

    settings = get_settings()
    idea = CompletedIdea(
        text=idea_text, start_ms=0, end_ms=0, segment_count=1, trigger=BoundaryTrigger.PAUSE,
    )
    session = SessionContext(uuid4(), UUID(classroom_id), "teacher", "room")
    evaluator = build_idea_evaluator(
        settings, build_retrieval_client(settings), build_brain_client(settings)
    )
    print(f"[live] RagService({settings.rag_base_url}) + Ollama({settings.eval_model})")
    print(f"idea: {idea_text}")
    print("-" * 72)
    _print_outcome(await evaluator.evaluate(idea, session))
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Idea evaluation check (LA-4).")
    parser.add_argument("--live", action="store_true", help="DEFERRED: use real clients.")
    parser.add_argument("--classroom", help="Classroom UUID (--live).")
    parser.add_argument("--idea", help="Teacher's explanation text (--live).")
    args = parser.parse_args(argv)

    if args.live:
        if not args.classroom or not args.idea:
            print("--live requires --classroom <uuid> and --idea <text>.", file=sys.stderr)
            return 1
        return asyncio.run(_run_live(args.classroom, args.idea))
    return asyncio.run(_run_offline())


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
