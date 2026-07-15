from __future__ import annotations

from enum import Enum


class FeedbackType(str, Enum):
    """Kind of problem the brain found in the teacher's explanation, relative to the
    course material.

    Pure domain enum (str-valued so it serializes cleanly). ``NONE`` means the
    explanation had no clear problem — the common, silence-biased case.

    - ``DISCREPANCY`` — a factual conflict with the material.
    - ``GAP``         — a missing/incomplete point the material covers.
    - ``UNCLEAR``     — an ambiguous or unclear statement.
    - ``NONE``        — no feedback warranted.
    """

    DISCREPANCY = "Discrepancy"
    GAP = "Gap"
    UNCLEAR = "Unclear"
    NONE = "None"
