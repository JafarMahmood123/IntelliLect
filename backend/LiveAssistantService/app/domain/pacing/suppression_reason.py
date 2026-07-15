from __future__ import annotations

from enum import Enum


class SuppressionReason(str, Enum):
    """Why the pacing gate (LA-7) withheld a suggestion, or ``NONE`` if delivered.

    Pure domain enum (str-valued so it serializes cleanly into logs/metrics).

    - ``RATE_LIMITED``   — too soon after the last delivered suggestion.
    - ``LOW_CONFIDENCE`` — the brain's confidence was below the threshold.
    - ``DUPLICATE``      — near-identical to a recently delivered suggestion.
    - ``SESSION_CAP``    — the per-session delivery cap is reached.
    - ``NONE``           — not suppressed (delivered).
    """

    RATE_LIMITED = "RateLimited"
    LOW_CONFIDENCE = "LowConfidence"
    DUPLICATE = "Duplicate"
    SESSION_CAP = "SessionCap"
    NONE = "None"
