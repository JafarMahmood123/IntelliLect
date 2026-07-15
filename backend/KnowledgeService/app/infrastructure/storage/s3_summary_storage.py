from __future__ import annotations

import asyncio
import logging

from app.application.ports.summary_storage import SummaryStorage
from app.infrastructure.config.settings import Settings

logger = logging.getLogger("knowledge.storage")


class S3SummaryStorage(SummaryStorage):
    """SummaryStorage that writes artifacts to S3-compatible object storage (boto3).

    Write counterpart of ``S3FileStorage``: same config style, but the SUMMARY_S3_*
    settings take precedence and fall back to the generic S3_* values, so summaries can
    live in their own bucket or share the recordings bucket. boto3 is synchronous, so
    each ``put_object`` runs in a worker thread; boto3 is imported lazily so the offline
    test suite never needs it.
    """

    def __init__(self, settings: Settings) -> None:
        self._bucket = settings.summary_s3_bucket or settings.s3_bucket_name
        self._client_kwargs = {
            "service_name": "s3",
            "endpoint_url": (settings.summary_s3_endpoint or settings.s3_service_url) or None,
            "aws_access_key_id": (settings.summary_s3_access_key or settings.s3_access_key) or None,
            "aws_secret_access_key": (settings.summary_s3_secret_key or settings.s3_secret_key) or None,
            "region_name": (settings.summary_s3_region or settings.s3_region) or None,
        }
        self._client = None  # lazily built on first upload

    def _get_client(self):
        if self._client is None:
            import boto3  # lazy: real S3 use only

            kwargs = {k: v for k, v in self._client_kwargs.items() if v is not None}
            self._client = boto3.client(**kwargs)
        return self._client

    async def upload(self, key: str, data: bytes, content_type: str) -> None:
        await asyncio.to_thread(self._put, key, data, content_type)

    def _put(self, key: str, data: bytes, content_type: str) -> None:
        client = self._get_client()
        client.put_object(
            Bucket=self._bucket, Key=key, Body=data, ContentType=content_type
        )
