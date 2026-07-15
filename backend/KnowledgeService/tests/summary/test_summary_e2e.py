"""End-to-end (mocked) coverage of the KnowledgeService summary half (S-0..S-3).

Wires a REAL SummaryGenerator (over fakes: transcript client, retrieval, brain) into a
REAL SummaryPipeline with fake storage + publisher, and a real WeasyPrint renderer when
its system libraries are present (otherwise a fake). Fully offline — no Ollama/S3/broker.
"""

from __future__ import annotations

from uuid import uuid4

from app.application.services.summary_pipeline import SummaryPipeline
from app.infrastructure.config.settings import Settings
from app.infrastructure.rendering.weasyprint_pdf_renderer import (
    WeasyPrintPdfRenderer,
    weasyprint_available,
)

from tests.summary.fakes import (
    DETERMINISTIC_MARKDOWN,
    FakeBrainClient,
    build_summary_generator,
    make_chunk,
)
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


def _renderer():
    """A real WeasyPrint renderer when the system libs are present, else a fake."""
    return WeasyPrintPdfRenderer() if weasyprint_available() else FakePdfRenderer()


class _RaisingBrain(FakeBrainClient):
    async def generate(self, system: str, prompt: str) -> str:
        raise RuntimeError("ollama exploded")


async def test_e2e_success_generates_uploads_and_publishes_both_artifacts():
    classroom_id = uuid4()
    session_id = uuid4()
    generator, transcript_client, _repo, _brain = build_summary_generator(
        transcript_text=_LECTURE,
        classroom_id=classroom_id,
        chunks=[make_chunk("Chlorophyll absorbs red and blue light.", page=2)],
    )
    storage = FakeSummaryStorage()
    publisher = FakeSummaryPublisher()
    pipeline = SummaryPipeline(
        generator, _renderer(), storage, publisher, Settings()
    )

    message = await pipeline.run(session_id)

    # Deterministic, templated keys.
    md_key = f"summaries/{classroom_id}/{session_id}.md"
    pdf_key = f"summaries/{classroom_id}/{session_id}.pdf"

    # The generated Markdown carries the stable S-1 section headings.
    uploaded = {key: (data, ct) for key, data, ct in storage.uploads}
    assert set(uploaded) == {md_key, pdf_key}
    md_text = uploaded[md_key][0].decode("utf-8")
    assert md_text == DETERMINISTIC_MARKDOWN
    for heading in ("# Session Summary", "## Overview", "## Key Points", "## Key Terms"):
        assert heading in md_text

    # Correct object keys, extensions, and content types for BOTH artifacts.
    assert md_key.endswith(".md")
    assert uploaded[md_key][1].startswith("text/markdown")
    assert pdf_key.endswith(".pdf")
    assert uploaded[pdf_key][1] == "application/pdf"
    assert uploaded[pdf_key][0].startswith(b"%PDF")

    # Exactly one success message carrying the keys + classroom_id.
    assert message.succeeded is True
    assert len(publisher.messages) == 1
    assert publisher.failures == []
    published = publisher.successes[0]
    assert published.md_s3_key == md_key
    assert published.pdf_s3_key == pdf_key
    assert published.classroom_id == classroom_id
    assert transcript_client.fetched_session_id == session_id


async def test_e2e_generation_failure_publishes_failure_uploads_nothing_no_raise():
    generator, _tc, _repo, _brain = build_summary_generator(
        transcript_text=_LECTURE,
        classroom_id=uuid4(),
        brain=_RaisingBrain(),
    )
    storage = FakeSummaryStorage()
    publisher = FakeSummaryPublisher()
    pipeline = SummaryPipeline(
        generator, _renderer(), storage, publisher, Settings()
    )
    session_id = uuid4()

    message = await pipeline.run(session_id)  # must NOT raise

    assert message.succeeded is False
    assert storage.uploads == []          # nothing uploaded
    assert publisher.successes == []      # no success message
    assert len(publisher.failures) == 1
    assert "ollama exploded" in message.error
