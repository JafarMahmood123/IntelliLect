from __future__ import annotations

from abc import ABC, abstractmethod


class SummaryStorage(ABC):
    """Write port for uploading summary artifacts (Markdown + PDF) to object storage.

    Implemented in the infrastructure layer over the S3 client. Bytes go STRAIGHT to
    S3 under a caller-supplied key — never proxied elsewhere. The application layer
    depends only on this abstraction, so the pipeline is tested with a fake that records
    uploads instead of a live bucket.
    """

    @abstractmethod
    async def upload(self, key: str, data: bytes, content_type: str) -> None:
        """Put ``data`` at ``key`` with ``content_type``, overwriting any existing object.

        Overwrite-on-put is what makes the pipeline idempotent per session: the keys are
        deterministic, so a re-run replaces the same objects rather than duplicating them.
        Raises if the upload cannot be completed.
        """
        raise NotImplementedError
