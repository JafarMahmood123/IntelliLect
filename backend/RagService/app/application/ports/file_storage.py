from __future__ import annotations

from abc import ABC, abstractmethod


class FileStorage(ABC):
    """Read-only port for fetching a document's raw bytes from object storage.

    Implemented in the infrastructure layer (S3-compatible storage). The ingestion
    service depends only on this abstraction, so it can be driven by fixture bytes
    in tests without any live bucket.
    """

    @abstractmethod
    async def get_bytes(self, s3_key: str) -> bytes:
        """Return the object's bytes, or raise if it cannot be fetched."""
        raise NotImplementedError
