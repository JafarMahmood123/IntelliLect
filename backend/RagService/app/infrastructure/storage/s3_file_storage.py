from __future__ import annotations

import asyncio
import logging

from app.application.ports.file_storage import FileStorage
from app.infrastructure.config.settings import Settings

logger = logging.getLogger("knowledge.storage")


class S3FileStorage(FileStorage):
    """Read-only FileStorage backed by S3-compatible object storage (boto3).

    Mirrors ClassroomService's S3FileStorageService config (service URL, bucket,
    region, credentials from S3_* settings). boto3 is a synchronous client, so each
    read runs in a worker thread to avoid blocking the event loop. boto3 is imported
    lazily so importing this module (and the app) needs no boto3 unless S3 is used —
    keeping the offline test suite dependency-free.
    """

    def __init__(self, settings: Settings) -> None:
        self._bucket = settings.s3_bucket_name
        self._client_kwargs = {
            "service_name": "s3",
            "endpoint_url": settings.s3_service_url or None,
            "aws_access_key_id": settings.s3_access_key or None,
            "aws_secret_access_key": settings.s3_secret_key or None,
            "region_name": settings.s3_region or None,
        }
        self._client = None  # lazily built on first read

    def _get_client(self):
        if self._client is None:
            import boto3  # lazy: real S3 use only

            kwargs = {k: v for k, v in self._client_kwargs.items() if v is not None}
            self._client = boto3.client(**kwargs)
        return self._client

    async def get_bytes(self, s3_key: str) -> bytes:
        return await asyncio.to_thread(self._download, s3_key)

    async def get_size(self, s3_key: str) -> int:
        return await asyncio.to_thread(self._head, s3_key)

    def _head(self, s3_key: str) -> int:
        """HEAD rather than GET: the whole point is to not pay for the body."""
        client = self._get_client()
        response = client.head_object(Bucket=self._bucket, Key=s3_key)
        return int(response["ContentLength"])

    def _download(self, s3_key: str) -> bytes:
        client = self._get_client()
        response = client.get_object(Bucket=self._bucket, Key=s3_key)
        try:
            return response["Body"].read()
        finally:
            response["Body"].close()
