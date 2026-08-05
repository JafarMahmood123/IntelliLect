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

    ``incorrect_text`` / ``corrected_text`` are the SPAN: the exact words the teacher said that
    are wrong, and what they should have been. A prose paragraph saying "you got the date wrong"
    cannot be coloured — highlighting a word in red requires knowing which word — so these carry
    the pointer the rendering needs.

    Both are optional and independent of ``text``, which always stands on its own:

    - The brain often finds a problem without being able to quote a clean span (a gap has nothing
      wrong to quote at all), so ``None`` is the ordinary case, not a degraded one.
    - ``incorrect_text`` is only ever set once it has been verified against what the teacher
      actually said. An unverified quote must not reach a client: highlighting words in red that
      the teacher never uttered is worse than showing no highlight.
    - ``corrected_text`` may be ``None`` while ``incorrect_text`` is set — knowing a claim is
      wrong does not require knowing the right answer. The reverse is not allowed: a correction
      with nothing to correct is not renderable.
    """

    text: str
    type: FeedbackType
    citations: list[int] = field(default_factory=list)
    sources: list[RetrievedChunk] = field(default_factory=list)
    confidence: float = 1.0
    incorrect_text: str | None = None
    corrected_text: str | None = None
