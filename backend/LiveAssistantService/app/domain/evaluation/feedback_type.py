from __future__ import annotations

from enum import Enum


class FeedbackType(str, Enum):
    """Kind of problem the brain found in the teacher's explanation, relative to the
    course material.

    Pure domain enum (str-valued so it serializes cleanly). ``NONE`` means the
    explanation had no clear problem — the common, silence-biased case.

    - ``DISCREPANCY`` — a factual conflict with the material.
    - ``GAP``         — a missing/incomplete point the material covers.
    - ``LIKELY``      — probably wrong, but not certainly: the brain is hedging.
    - ``NONE``        — no feedback warranted.

    ``LIKELY`` replaced the older ``UNCLEAR``. The rename is not cosmetic — it moved the category
    from a property of the WORDING ("you said that ambiguously") to a statement of the brain's own
    certainty ("this looks wrong, but I would not assert it"). That is the thing a teacher can act
    on mid-lecture, and it is what the amber colour means in the UI.
    """

    DISCREPANCY = "Discrepancy"
    GAP = "Gap"
    LIKELY = "Likely"
    NONE = "None"
