"""Seed classroom material: build a small PDF and upload it to MinIO.

KnowledgeService ingests PDF/DOCX/PPTX pulled from S3 by key, so to give the
classroom something for retrieval to find we (1) render a PDF stating a clear fact,
(2) upload it to the shared bucket, then the test ingests it by that key. The teacher
later contradicts the fact, so retrieval returns this document and the brain raises a
discrepancy suggestion.
"""

from __future__ import annotations

import io

from minio import Minio
from reportlab.lib.pagesizes import letter
from reportlab.pdfgen import canvas


def make_pdf_bytes(title: str, paragraphs: list[str]) -> bytes:
    buf = io.BytesIO()
    c = canvas.Canvas(buf, pagesize=letter)
    text = c.beginText(72, 720)
    text.setFont("Helvetica-Bold", 16)
    text.textLine(title)
    text.textLine("")
    text.setFont("Helvetica", 12)
    for para in paragraphs:
        # Wrap long paragraphs crudely so lines fit the page.
        for i in range(0, len(para), 90):
            text.textLine(para[i : i + 90])
        text.textLine("")
    c.drawText(text)
    c.showPage()
    c.save()
    return buf.getvalue()


class MinioSeeder:
    def __init__(
        self,
        endpoint: str,
        access_key: str,
        secret_key: str,
        *,
        secure: bool,
        bucket: str,
    ) -> None:
        self._client = Minio(endpoint, access_key=access_key, secret_key=secret_key, secure=secure)
        self._bucket = bucket

    def ensure_bucket(self) -> None:
        if not self._client.bucket_exists(self._bucket):
            self._client.make_bucket(self._bucket)

    def put(self, key: str, data: bytes, content_type: str) -> str:
        self.ensure_bucket()
        self._client.put_object(
            self._bucket,
            key,
            io.BytesIO(data),
            length=len(data),
            content_type=content_type,
        )
        return key
