"""LiveAssistantService client: observe the agent session + transcript + metrics.

Hit directly on its published host port with the shared X-Internal-Secret (it is
not exposed through the gateway). This is how the test observes the server side of
the AI loop: that the session registered, that the transcript persisted, and — via
Prometheus counters — that ideas were detected/evaluated and feedback delivered.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

import httpx

from clients.http import expect_ok, get_ci


@dataclass
class Transcript:
    session_id: str
    classroom_id: str
    status: str
    segment_count: int
    text: str


class LiveAssistantClient:
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

    def active_sessions(self) -> list[str]:
        resp = expect_ok(self._http.get("/api/internal/sessions"))
        return [str(s) for s in get_ci(resp.json(), "activeSessions", [])]

    def is_active(self, session_id: str) -> bool:
        return str(session_id) in self.active_sessions()

    def get_transcript(self, session_id: str) -> Transcript | None:
        resp = self._http.get(f"/api/internal/sessions/{session_id}/transcript")
        if resp.status_code == 404:
            return None
        expect_ok(resp)
        data = resp.json()
        return Transcript(
            session_id=str(get_ci(data, "sessionId")),
            classroom_id=str(get_ci(data, "classroomId")),
            status=str(get_ci(data, "status")),
            segment_count=int(get_ci(data, "segmentCount", 0)),
            text=str(get_ci(data, "text", "")),
        )

    def stop_session(self, session_id: str) -> None:
        resp = self._http.post(f"/api/internal/sessions/{session_id}/stop")
        expect_ok(resp)

    # --- metrics -------------------------------------------------------------
    def metrics(self) -> str:
        return expect_ok(self._http.get("/metrics")).text

    def counter_total(self, metric: str) -> float:
        """Sum every sample of a Prometheus counter across all label sets.

        e.g. counter_total("suggestions_delivered_total") sums delivered feedback
        over all `type=` labels. Returns 0.0 if the metric is absent.
        """
        text = self.metrics()
        total = 0.0
        # Match `metric_name{labels} value` or `metric_name value`.
        pattern = re.compile(
            r"^" + re.escape(metric) + r"(?:\{[^}]*\})?\s+([0-9eE.+-]+)\s*$",
            re.MULTILINE,
        )
        for match in pattern.finditer(text):
            try:
                total += float(match.group(1))
            except ValueError:
                continue
        return total
