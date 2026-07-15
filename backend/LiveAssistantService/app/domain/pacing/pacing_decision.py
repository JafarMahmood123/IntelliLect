from __future__ import annotations

from dataclasses import dataclass

from app.domain.pacing.suppression_reason import SuppressionReason


@dataclass(frozen=True)
class PacingDecision:
    """The pacing gate's verdict for one suggestion (LA-7).

    Pure domain object. ``deliver`` is whether the suggestion should reach the teacher;
    ``reason`` is ``NONE`` when delivered, otherwise why it was suppressed.
    """

    deliver: bool
    reason: SuppressionReason

    @classmethod
    def delivered(cls) -> "PacingDecision":
        return cls(deliver=True, reason=SuppressionReason.NONE)

    @classmethod
    def suppressed(cls, reason: SuppressionReason) -> "PacingDecision":
        return cls(deliver=False, reason=reason)
