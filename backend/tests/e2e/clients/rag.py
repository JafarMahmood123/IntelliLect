"""RagService client: ingest classroom material + search.

Hit directly on its published host port with the shared X-Internal-Secret (not
exposed through the gateway). We seed the classroom with a PDF so that when the
teacher later contradicts it, retrieval returns the source chunks and the brain can
raise a "discrepancy" suggestion (otherwise retrieval short-circuits -> no feedback).
"""

from __future__ import annotations

import httpx

from clients.http import expect_ok, get_ci


class RagClient:
    def __init__(self, base_url: str, internal_secret: str, timeout_s: float) -> None:
        self._http = httpx.Client(
            base_url=base_url,
            timeout=timeout_s,
            headers={"X-Internal-Secret": internal_secret},
        )

    def close(self) -> None:
        self._http.close()

    def healthy(self) -> bool:
        return self._http.get("/health").is_success

    def ingest(
        self,
        *,
        file_id: str,
        classroom_id: str,
        s3_key: str,
        file_name: str,
        content_type: str,
        size_bytes: int,
    ) -> str:
        body = {
            "fileId": file_id,
            "classroomId": classroom_id,
            "s3Key": s3_key,
            "fileName": file_name,
            "contentType": content_type,
            "sizeBytes": size_bytes,
        }
        resp = expect_ok(self._http.post("/api/internal/documents/ingest", json=body))
        return str(get_ci(resp.json(), "status"))

    def document_status(self, file_id: str) -> str:
        resp = self._http.get(f"/api/internal/documents/{file_id}/status")
        if resp.status_code == 404:
            return "Unknown"
        expect_ok(resp)
        return str(get_ci(resp.json(), "status"))

    def search(self, classroom_id: str, query: str, top_k: int = 6) -> list[dict]:
        resp = expect_ok(
            self._http.post(
                "/api/search",
                json={"classroomId": classroom_id, "query": query, "topK": top_k},
            )
        )
        return get_ci(resp.json(), "results", [])
