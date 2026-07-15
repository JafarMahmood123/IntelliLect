from __future__ import annotations

import logging
import time
from uuid import UUID

from app.application.dtos.summary_messages import SessionSummaryReadyMessage
from app.application.ports.pdf_renderer import PdfRenderer, SummaryPdfMetadata
from app.application.ports.summary_publisher import SummaryPublisher
from app.application.ports.summary_storage import SummaryStorage
from app.application.services.summary_generator import SummaryGenerator
from app.observability import metrics

logger = logging.getLogger("knowledge.summary")

_MARKDOWN_CONTENT_TYPE = "text/markdown; charset=utf-8"
_PDF_CONTENT_TYPE = "application/pdf"


class SummaryPipeline:
    """End-to-end session-summary orchestration (S-3).

    On a session-end trigger: generate the Markdown (S-1) -> render the PDF (S-2) ->
    upload BOTH artifacts to object storage -> publish a ``SessionSummaryReadyMessage``
    for ClassroomService (S-4). It NEVER raises: any step failing publishes a failure
    message (so S-4 can mark the summary Failed) and is logged. Idempotent per session —
    the S3 keys are deterministic (templated by session), so a re-run overwrites the
    same objects and safely re-publishes rather than duplicating artifacts.

    Pure orchestration over ports; runs fully offline against fakes.
    """

    def __init__(
        self,
        generator: SummaryGenerator,
        renderer: PdfRenderer,
        storage: SummaryStorage,
        publisher: SummaryPublisher,
        settings,
    ) -> None:
        self._generator = generator
        self._renderer = renderer
        self._storage = storage
        self._publisher = publisher
        self._key_template = settings.summary_s3_key_template

    async def run(
        self, session_id: UUID, classroom_id: UUID | None = None
    ) -> SessionSummaryReadyMessage:
        """Produce + store + announce a session's summary. Returns the published message."""
        known_classroom = classroom_id
        try:
            result = await self._generator.generate(session_id)
            known_classroom = result.classroom_id

            metadata = SummaryPdfMetadata(
                title="Session Summary",
                session_date=result.generated_at.date() if result.generated_at else None,
                classroom_name=None,  # only the classroom_id is known at this layer
                generated_at=result.generated_at,
            )
            render_started = time.perf_counter()
            pdf_bytes = self._renderer.render(result.markdown, metadata)
            metrics.observe_summary_render(time.perf_counter() - render_started)
            logger.info(
                "summary_pdf_rendered",
                extra={"session_id": str(session_id), "size_bytes": len(pdf_bytes)},
            )

            md_key = self._key(result.classroom_id, session_id, "md")
            pdf_key = self._key(result.classroom_id, session_id, "pdf")
            await self._storage.upload(
                md_key, result.markdown.encode("utf-8"), _MARKDOWN_CONTENT_TYPE
            )
            await self._storage.upload(pdf_key, pdf_bytes, _PDF_CONTENT_TYPE)
            logger.info(
                "summary_artifacts_uploaded",
                extra={"session_id": str(session_id), "md_key": md_key, "pdf_key": pdf_key},
            )

            message = SessionSummaryReadyMessage.success(
                session_id=session_id,
                classroom_id=result.classroom_id,
                md_s3_key=md_key,
                pdf_s3_key=pdf_key,
                generated_at=result.generated_at,
            )
            await self._publisher.publish_ready(message)
            logger.info(
                "summary_ready_published",
                extra={"session_id": str(session_id), "succeeded": True},
            )
            logger.info(
                "Summary ready for session %s (classroom %s): %s, %s",
                session_id, result.classroom_id, md_key, pdf_key,
            )
            metrics.record_summary_generated()
            return message
        except Exception as exc:  # noqa: BLE001 — summary must be non-fatal to session end
            metrics.record_summary_failed()
            logger.error(
                "Summary pipeline failed for session %s: %s: %s",
                session_id, type(exc).__name__, exc,
            )
            return await self._publish_failure(session_id, known_classroom, str(exc))

    async def _publish_failure(
        self, session_id: UUID, classroom_id: UUID | None, error: str
    ) -> SessionSummaryReadyMessage:
        message = SessionSummaryReadyMessage.failure(session_id, classroom_id, error)
        try:
            await self._publisher.publish_ready(message)
        except Exception:  # noqa: BLE001 — even the failure notice must not crash the run
            logger.exception(
                "Failed to publish the FAILURE summary message for session %s.", session_id
            )
        return message

    def _key(self, classroom_id: UUID, session_id: UUID, ext: str) -> str:
        return self._key_template.format(
            classroom_id=classroom_id, session_id=session_id, ext=ext
        )
