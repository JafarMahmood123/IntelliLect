"""Prometheus metrics wiring for the summary pipeline (S-5).

Uses the REGISTRY.get_sample_value delta pattern from tests/observability/test_metrics.py:
capture "before" values, run the pipeline, assert the counters/histograms moved.
"""

from __future__ import annotations

from uuid import uuid4

from prometheus_client import REGISTRY

from app.application.services.summary_pipeline import SummaryPipeline
from app.infrastructure.config.settings import Settings
from app.observability import metrics

from tests.summary.fakes import FakeBrainClient, build_summary_generator, make_chunk
from tests.summary.pipeline_fakes import (
    FakePdfRenderer,
    FakeSummaryPublisher,
    FakeSummaryStorage,
)

_LECTURE = (
    "Today we covered photosynthesis, the process by which plants turn light energy "
    "into glucose inside chloroplasts using chlorophyll, across the light-dependent "
    "reactions and the Calvin cycle."
)


def _value(name: str, labels: dict | None = None) -> float:
    return REGISTRY.get_sample_value(name, labels or {}) or 0.0


class _RaisingBrain(FakeBrainClient):
    async def generate(self, system: str, prompt: str) -> str:
        raise RuntimeError("ollama exploded")


async def test_successful_run_moves_summary_counters_and_histograms():
    metrics.set_enabled(True)
    generator, _tc, _repo, _brain = build_summary_generator(
        transcript_text=_LECTURE,
        classroom_id=uuid4(),
        # Grounding is enabled by default; a returned chunk makes the summary "grounded".
        chunks=[make_chunk("Chlorophyll absorbs red and blue light.")],
        settings=Settings(summary_grounding_enabled=True),
    )
    pipeline = SummaryPipeline(
        generator, FakePdfRenderer(), FakeSummaryStorage(), FakeSummaryPublisher(),
        Settings(),
    )

    before = {
        "generated": _value("summaries_generated_total"),
        "grounded": _value("summaries_grounded_total"),
        "gen_count": _value("summary_generation_seconds_count"),
        "render_count": _value("summary_render_seconds_count"),
        "tokens_count": _value("summary_transcript_tokens_count"),
    }

    message = await pipeline.run(uuid4())
    assert message.succeeded is True

    assert _value("summaries_generated_total") == before["generated"] + 1
    assert _value("summaries_grounded_total") == before["grounded"] + 1
    assert _value("summary_generation_seconds_count") == before["gen_count"] + 1
    assert _value("summary_render_seconds_count") == before["render_count"] + 1
    assert _value("summary_transcript_tokens_count") == before["tokens_count"] + 1


async def test_failed_run_moves_failed_counter():
    metrics.set_enabled(True)
    generator, _tc, _repo, _brain = build_summary_generator(
        transcript_text=_LECTURE, classroom_id=uuid4(), brain=_RaisingBrain(),
    )
    pipeline = SummaryPipeline(
        generator, FakePdfRenderer(), FakeSummaryStorage(), FakeSummaryPublisher(),
        Settings(),
    )

    before_failed = _value("summaries_failed_total")

    message = await pipeline.run(uuid4())
    assert message.succeeded is False

    assert _value("summaries_failed_total") == before_failed + 1
