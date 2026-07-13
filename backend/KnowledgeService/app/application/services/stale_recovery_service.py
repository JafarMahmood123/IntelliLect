from __future__ import annotations

import logging
from datetime import timedelta

from app.application.ports.document_repository import DocumentRepository
from app.application.services.clock import Clock, SystemClock
from app.domain.entities.document import Document

logger = logging.getLogger("knowledge.ingestion.recovery")


class StaleRecoveryService:
    """Recovers documents left stuck in Processing by a crash/restart.

    Finds Processing documents whose processing_started_at is older than the stale
    threshold, resets them to Pending (keeping their attempt count), and returns them
    so the caller can re-enqueue. Runs on startup when STALE_RECOVERY_ON_STARTUP.
    """

    def __init__(
        self,
        document_repository: DocumentRepository,
        clock: Clock | None = None,
        stale_minutes: int = 15,
    ) -> None:
        self._documents = document_repository
        self._clock = clock or SystemClock()
        self._stale_minutes = stale_minutes

    async def recover(self) -> list[Document]:
        threshold = self._clock.now() - timedelta(minutes=self._stale_minutes)
        stale = await self._documents.find_stale_processing(threshold)
        recovered: list[Document] = []
        for document in stale:
            reset = await self._documents.reset_to_pending(
                document.file_id, reset_attempts=False
            )
            if reset is not None:
                recovered.append(reset)
        if recovered:
            logger.info(
                "Recovered %d stale Processing document(s) older than %d min.",
                len(recovered),
                self._stale_minutes,
            )
        return recovered
