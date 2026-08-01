"""A quiz the brain generated from what the teacher just explained.

Pure domain objects: no library imports. This is a PROPOSAL, not a quiz — it carries no ids, no
state and no marks, because it is handed to ClassroomService to become a Draft the teacher reviews
and edits before anything reaches a student.

Marks and per-question time are deliberately absent. How much a question is worth is a
pedagogical judgement the teacher owns, and the server already has a default seconds-per-question;
letting the model invent either would add a validation surface and a way to disagree with the
configured limits, for no benefit.
"""

from __future__ import annotations

from dataclasses import dataclass, field


@dataclass(frozen=True)
class GeneratedOption:
    text: str
    is_correct: bool


@dataclass(frozen=True)
class GeneratedCorrection:
    """A point where the course material contradicts what the teacher said.

    The quiz is written to match the MATERIAL, so a class is never marked against a mistake. This
    records the disagreement so the teacher finds out — silently flipping an answer key would be
    worse than the original error, because the teacher would publish a quiz whose correct answer
    contradicts what they told the room a minute earlier and never know why students protested.

    Both sides are quoted rather than summarised: "you said X, the material says Y" is checkable by
    the teacher in a second, and a paraphrase of their own words is not.
    """

    taught: str
    corrected: str


@dataclass(frozen=True)
class GeneratedQuestion:
    text: str
    options: list[GeneratedOption]
    #: Only populated on the answers-for-my-question path, where the question is the carrier of the
    #: whole reply. Questions nested inside a ``GeneratedQuiz`` leave this empty — the quiz holds
    #: the corrections for that path, so there is exactly one place to read them per request.
    corrections: list[GeneratedCorrection] = field(default_factory=list)

    @property
    def correct_count(self) -> int:
        return sum(1 for option in self.options if option.is_correct)


@dataclass(frozen=True)
class GeneratedQuiz:
    """The generated proposal, plus how it was grounded.

    ``citations`` are the 1-based indices of the retrieved chunks the model said it used, in the
    same numbering the evaluation prompt uses. ``grounded`` is False when the quiz was written from
    the teacher's words alone because retrieval found nothing relevant — worth surfacing, since an
    ungrounded quiz is the one most worth the teacher's attention before publishing.
    """

    title: str
    questions: list[GeneratedQuestion]
    citations: list[int] = field(default_factory=list)
    grounded: bool = True
    #: Where the material contradicted the explanation. Empty is the normal case.
    corrections: list[GeneratedCorrection] = field(default_factory=list)

    @property
    def is_empty(self) -> bool:
        return not self.questions
