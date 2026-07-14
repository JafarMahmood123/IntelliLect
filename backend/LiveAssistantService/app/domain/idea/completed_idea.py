from __future__ import annotations

from dataclasses import dataclass

from app.domain.idea.boundary_trigger import BoundaryTrigger


@dataclass(frozen=True)
class CompletedIdea:
    """One coherent "idea" the teacher finished, emitted when a boundary fires.

    Pure domain object: no library imports. The buffer that produced it holds ordered
    ``TranscriptSegment`` references (LA-2); this is the flattened, downstream-ready
    result. ``representative_vector`` is the running idea embedding (mean of the
    buffer's segment vectors) at close time — optional, and may be recomputed
    downstream (LA-4 retrieval).

    This phase only *detects and emits* ideas; it does not retrieve, evaluate, or
    give feedback.
    """

    text: str  # concatenated idea text
    start_ms: int
    end_ms: int
    segment_count: int
    trigger: BoundaryTrigger
    representative_vector: list[float] | None = None

    @property
    def duration_ms(self) -> int:
        return max(0, self.end_ms - self.start_ms)
