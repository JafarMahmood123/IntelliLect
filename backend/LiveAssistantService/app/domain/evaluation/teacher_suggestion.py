from __future__ import annotations

from dataclasses import dataclass, field

from app.domain.evaluation.feedback_type import FeedbackType
from app.domain.evaluation.retrieved_chunk import RetrievedChunk


@dataclass(frozen=True)
class TeacherSuggestion:
    """A single grounded suggestion for the teacher, produced by the brain.

    Pure domain object. ``citations`` are the 1-based reference numbers the model
    cited (``[n]``) in ``text``; ``sources`` are the corresponding ``RetrievedChunk``s
    those numbers map to. ``confidence`` is the brain's self-reported confidence in
    [0, 1] (LA-7 pacing suppresses low-confidence suggestions); it defaults to 1.0 so
    suggestions built without one are treated as fully confident.
    """

    text: str
    type: FeedbackType
    citations: list[int] = field(default_factory=list)
    sources: list[RetrievedChunk] = field(default_factory=list)
    confidence: float = 1.0
