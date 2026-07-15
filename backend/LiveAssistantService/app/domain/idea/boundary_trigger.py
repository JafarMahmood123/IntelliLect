from __future__ import annotations

from enum import Enum


class BoundaryTrigger(str, Enum):
    """Why the boundary detector closed an idea buffer.

    Pure domain enum (str-valued so it serializes cleanly downstream).

    - ``DRIFT``     — the newest segment was semantically far from the running idea.
    - ``PAUSE``     — a speech pause (the LA-2 ``followed_by_pause`` flag, or a silent
                      gap between segments) marked a thought break. Also used for the
                      end-of-stream flush: session end is a terminal pause.
    - ``TIME_CAP``  — safety net: accumulated speech exceeded ``BOUNDARY_MAX_SECONDS``.
    - ``TOKEN_CAP`` — safety net: accumulated text exceeded ``BOUNDARY_MAX_TOKENS``.
    """

    DRIFT = "Drift"
    PAUSE = "Pause"
    TIME_CAP = "TimeCap"
    TOKEN_CAP = "TokenCap"
