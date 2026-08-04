"""Offline fakes for the S-3 summary pipeline (no models, S3, or broker)."""

from __future__ import annotations

from datetime import datetime, timezone
from uuid import UUID, uuid4

from app.application.dtos.summary_dtos import SummaryResult
from app.application.dtos.summary_messages import SessionSummaryReadyMessage
from app.application.ports.pdf_renderer import PdfRenderer, SummaryPdfMetadata
from app.application.ports.summary_publisher import SummaryPublisher
from app.application.ports.summary_storage import SummaryStorage

_FIXED_MARKDOWN = "# Session Summary\n\n## Overview\nA fixed summary.\n"
_FIXED_PDF = b"%PDF-1.7\nfake-pdf-bytes\n%%EOF"
_FIXED_TIME = datetime(2026, 7, 15, 9, 30, tzinfo=timezone.utc)


class FakeSummaryGenerator:
    """Returns a fixed SummaryResult; can be told to raise to test failure handling."""

    def __init__(
        self,
        *,
        classroom_id: UUID | None = None,
        markdown: str = _FIXED_MARKDOWN,
        raises: Exception | None = None,
    ) -> None:
        self.classroom_id = classroom_id or uuid4()
        self._markdown = markdown
        self._raises = raises
        self.calls: list[UUID] = []

    async def generate(self, session_id: UUID) -> SummaryResult:
        self.calls.append(session_id)
        if self._raises is not None:
            raise self._raises
        return SummaryResult(
            session_id=session_id,
            classroom_id=self.classroom_id,
            markdown=self._markdown,
            model="fake-model",
            generated_at=_FIXED_TIME,
        )


class FakePdfRenderer(PdfRenderer):
    """Returns fixed PDF bytes; records the metadata; can raise on demand."""

    def __init__(self, *, pdf: bytes = _FIXED_PDF, raises: Exception | None = None) -> None:
        self._pdf = pdf
        self._raises = raises
        self.calls: list[tuple[str, SummaryPdfMetadata]] = []

    def render(self, markdown: str, metadata: SummaryPdfMetadata) -> bytes:
        self.calls.append((markdown, metadata))
        if self._raises is not None:
            raise self._raises
        return self._pdf


class FakeSummaryStorage(SummaryStorage):
    """Records every upload; can raise to simulate an S3 failure."""

    def __init__(self, *, raises: Exception | None = None) -> None:
        self._raises = raises
        self.uploads: list[tuple[str, bytes, str]] = []

    async def upload(self, key: str, data: bytes, content_type: str) -> None:
        if self._raises is not None:
            raise self._raises
        self.uploads.append((key, data, content_type))

    @property
    def keys(self) -> list[str]:
        return [key for key, _data, _ct in self.uploads]


class FakeSummaryPublisher(SummaryPublisher):
    """Records every published message; can raise to simulate a broker failure."""

    def __init__(self, *, raises: Exception | None = None) -> None:
        self._raises = raises
        self.messages: list[SessionSummaryReadyMessage] = []

    async def publish_ready(self, message: SessionSummaryReadyMessage) -> None:
        if self._raises is not None:
            raise self._raises
        self.messages.append(message)

    @property
    def successes(self) -> list[SessionSummaryReadyMessage]:
        return [m for m in self.messages if m.succeeded]

    @property
    def failures(self) -> list[SessionSummaryReadyMessage]:
        return [m for m in self.messages if not m.succeeded]
