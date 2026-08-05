from __future__ import annotations

from enum import Enum

from app.domain.evaluation.feedback_type import FeedbackType


class FeedbackSeverity(str, Enum):
    """How firmly the brain is making its claim — the presentation semantic the UI colours by.

    Separate from :class:`FeedbackType` on purpose. ``FeedbackType`` answers "what kind of problem
    is this?"; ``FeedbackSeverity`` answers "how sure are we, and how loudly should this be shown?"
    They map one-to-one today, but the frontend must not be the place that decides a colour: adding
    a fifth feedback type should change the meaning here, not force every client to re-derive a
    palette from a diagnostic label it does not own.

    - ``INCORRECT`` — asserted wrong. Red, and normally carries the offending span.
    - ``LIKELY``    — probably wrong, hedged. Amber.
    - ``MISSING``   — nothing said was wrong; something was left out. Neutral.
    """

    INCORRECT = "Incorrect"
    LIKELY = "Likely"
    MISSING = "Missing"


_SEVERITY_BY_TYPE = {
    FeedbackType.DISCREPANCY: FeedbackSeverity.INCORRECT,
    FeedbackType.LIKELY: FeedbackSeverity.LIKELY,
    FeedbackType.GAP: FeedbackSeverity.MISSING,
}


def severity_of(feedback_type: FeedbackType) -> FeedbackSeverity:
    """The severity a feedback type is shown at.

    ``NONE`` never reaches a client — a no-feedback outcome carries no suggestion at all — so it
    has no severity of its own and falls back to the quietest one rather than raising. Nothing
    downstream should have to handle an exception on a display concern.
    """
    return _SEVERITY_BY_TYPE.get(feedback_type, FeedbackSeverity.MISSING)
