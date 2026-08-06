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


@dataclass(frozen=True)
class HistogramSnapshot:
    """One reading of a Prometheus histogram. Cumulative — subtract two to get a run."""

    metric: str
    buckets: list[tuple[float, float]]  # (upper bound seconds, cumulative count)
    total: float  # _sum, seconds
    count: float  # _count, observations

    def __sub__(self, earlier: "HistogramSnapshot") -> "HistogramSnapshot":
        earlier_by_edge = dict(earlier.buckets)
        return HistogramSnapshot(
            metric=self.metric,
            buckets=[(edge, value - earlier_by_edge.get(edge, 0.0)) for edge, value in self.buckets],
            total=self.total - earlier.total,
            count=self.count - earlier.count,
        )

    @property
    def mean_seconds(self) -> float:
        return self.total / self.count if self.count else float("nan")

    def quantile_seconds(self, fraction: float) -> float:
        """The bucket upper bound at or above the requested quantile.

        Deliberately NOT interpolated. Prometheus' own histogram_quantile interpolates
        within a bucket and thereby reports values that were never observed; with these
        bucket widths that is a fiction. Returning the bucket edge says "at most this",
        which is the only claim the data supports.
        """
        if not self.count or not self.buckets:
            return float("nan")
        target = fraction * self.count
        for edge, cumulative in self.buckets:
            if cumulative >= target:
                return edge
        return self.buckets[-1][0]


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

    def get_feedback(self, session_id: str) -> list[dict]:
        """Feedback suggestions delivered for a session (fake-audio mode). [] if none."""
        resp = self._http.get(f"/api/internal/sessions/{session_id}/feedback")
        if resp.status_code == 404:
            return []
        expect_ok(resp)
        return get_ci(resp.json(), "feedback", [])

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

    # --- histograms (§9) ------------------------------------------------------

    def histogram(self, metric: str) -> "HistogramSnapshot":
        """Read a Prometheus histogram's cumulative buckets, sum and count.

        This is how the assistant hop (L-3) is measured: the pipeline already times
        itself in-process (`app/observability/metrics.py`), which is strictly better
        than timing it from outside — every stage boundary is on one clock, and there
        is no host-to-container skew to subtract.

        The catch, and the reason ``HistogramSnapshot`` supports subtraction: these
        are cumulative since the process started. Reporting them raw would mix this
        run's numbers with every earlier one, including runs made before a fix.
        """
        text = self.metrics()
        buckets: list[tuple[float, float]] = []
        total = 0.0
        count = 0.0
        for line in text.splitlines():
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            name, _, value = line.rpartition(" ")
            try:
                numeric = float(value)
            except ValueError:
                continue
            if name.startswith(f"{metric}_bucket"):
                edge = re.search(r'le="([^"]+)"', name)
                if edge:
                    upper = float("inf") if edge.group(1) == "+Inf" else float(edge.group(1))
                    buckets.append((upper, numeric))
            elif name == f"{metric}_sum":
                total = numeric
            elif name == f"{metric}_count":
                count = numeric
        return HistogramSnapshot(metric=metric, buckets=sorted(buckets), total=total, count=count)
