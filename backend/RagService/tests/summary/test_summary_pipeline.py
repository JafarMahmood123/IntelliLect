"""SummaryPipeline (S-3) — happy path, failure, idempotency, non-fatal. All offline."""

from __future__ import annotations

from uuid import uuid4

from app.application.services.summary_pipeline import SummaryPipeline
from app.infrastructure.config.settings import Settings

from tests.summary.pipeline_fakes import (
    FakePdfRenderer,
    FakeSummaryGenerator,
    FakeSummaryPublisher,
    FakeSummaryStorage,
)


def _pipeline(*, generator=None, renderer=None, storage=None, publisher=None):
    settings = Settings()  # default SUMMARY_S3_KEY_TEMPLATE
    generator = generator or FakeSummaryGenerator()
    renderer = renderer or FakePdfRenderer()
    storage = storage or FakeSummaryStorage()
    publisher = publisher or FakeSummaryPublisher()
    pipeline = SummaryPipeline(generator, renderer, storage, publisher, settings)
    return pipeline, generator, renderer, storage, publisher


async def test_happy_path_uploads_both_artifacts_and_publishes_success():
    generator = FakeSummaryGenerator()
    pipeline, _gen, _rend, storage, publisher = _pipeline(generator=generator)
    session_id = uuid4()

    message = await pipeline.run(session_id)

    classroom = generator.classroom_id
    md_key = f"summaries/{classroom}/{session_id}.md"
    pdf_key = f"summaries/{classroom}/{session_id}.pdf"

    # Both artifacts uploaded under the templated keys with correct content types.
    assert len(storage.uploads) == 2
    uploaded = {key: (data, ct) for key, data, ct in storage.uploads}
    assert set(uploaded) == {md_key, pdf_key}
    assert uploaded[md_key][1].startswith("text/markdown")
    assert uploaded[md_key][0] == b"# Session Summary\n\n## Overview\nA fixed summary.\n"
    assert uploaded[pdf_key][1] == "application/pdf"
    assert uploaded[pdf_key][0].startswith(b"%PDF")

    # A single success message carrying both keys.
    assert len(publisher.messages) == 1
    assert message.succeeded is True
    assert publisher.successes[0].md_s3_key == md_key
    assert publisher.successes[0].pdf_s3_key == pdf_key
    assert publisher.successes[0].classroom_id == classroom
    assert publisher.failures == []


async def test_generator_failure_publishes_failure_and_does_not_crash():
    generator = FakeSummaryGenerator(raises=RuntimeError("brain down"))
    pipeline, _g, _r, storage, publisher = _pipeline(generator=generator)
    session_id, classroom_id = uuid4(), uuid4()

    message = await pipeline.run(session_id, classroom_id)  # must NOT raise

    assert storage.uploads == []            # nothing uploaded
    assert publisher.successes == []        # no success message
    assert len(publisher.failures) == 1
    assert message.succeeded is False
    assert message.classroom_id == classroom_id  # caller-supplied classroom preserved
    assert "brain down" in message.error


async def test_renderer_failure_publishes_failure():
    pipeline, _g, _r, storage, publisher = _pipeline(
        renderer=FakePdfRenderer(raises=RuntimeError("render boom"))
    )

    message = await pipeline.run(uuid4())

    assert storage.uploads == []
    assert message.succeeded is False
    assert len(publisher.failures) == 1
    assert "render boom" in message.error


async def test_upload_failure_publishes_failure():
    pipeline, _g, _r, _s, publisher = _pipeline(
        storage=FakeSummaryStorage(raises=RuntimeError("s3 unavailable"))
    )

    message = await pipeline.run(uuid4())

    assert message.succeeded is False
    assert len(publisher.failures) == 1
    assert "s3 unavailable" in message.error


async def test_publisher_failure_is_swallowed_and_does_not_crash():
    # Even if publishing the FAILURE itself fails, run() must not raise.
    pipeline, _g, _r, _s, _p = _pipeline(
        generator=FakeSummaryGenerator(raises=RuntimeError("gen down")),
        publisher=FakeSummaryPublisher(raises=RuntimeError("broker down")),
    )

    message = await pipeline.run(uuid4())  # no exception propagates
    assert message.succeeded is False


async def test_idempotent_rerun_uses_same_keys_and_republishes():
    generator = FakeSummaryGenerator()
    pipeline, _g, _r, storage, publisher = _pipeline(generator=generator)
    session_id = uuid4()

    first = await pipeline.run(session_id)
    second = await pipeline.run(session_id)

    # Same deterministic keys both times -> overwrite, not duplicate artifacts.
    assert first.md_s3_key == second.md_s3_key
    assert first.pdf_s3_key == second.pdf_s3_key
    assert set(storage.keys) == {first.md_s3_key, first.pdf_s3_key}  # 2 distinct keys
    assert len(storage.uploads) == 4  # 2 per run (same keys overwritten)
    assert len(publisher.successes) == 2  # safe re-publish


async def test_empty_transcript_minimal_summary_is_success_not_failure():
    # S-1 returns a valid minimal "insufficient content" Markdown (never raises), so the
    # pipeline treats it as a normal success and still uploads + publishes.
    generator = FakeSummaryGenerator(markdown="# Session Summary\n\n_Insufficient content._\n")
    pipeline, _g, _r, storage, publisher = _pipeline(generator=generator)

    message = await pipeline.run(uuid4())

    assert message.succeeded is True
    assert len(storage.uploads) == 2
    assert len(publisher.successes) == 1
