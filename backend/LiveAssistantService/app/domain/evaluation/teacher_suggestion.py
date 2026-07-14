from __future__ import annotations

from dataclasses import dataclass, field

from app.domain.evaluation.feedback_type import FeedbackType
from app.domain.evaluation.retrieved_chunk import RetrievedChunk


@dataclass(frozen=True)
class TeacherSuggestion:
    """A single grounded suggestion for the teacher, produced by the brain.

    Pure domain object. ``citations`` are the 1-based reference numbers the model
    cited (``[n]``) in ``text``; ``sources`` are the corresponding ``RetrievedChunk``s
    those numbers map to. This phase produces the suggestion only — it is NOT
    delivered to the teacher (that is LA-5).
    """

    text: str
    type: FeedbackType
    citations: list[int] = field(default_factory=list)
    sources: list[RetrievedChunk] = field(default_factory=list)
