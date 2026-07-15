from __future__ import annotations

from enum import Enum


class TranscriptStatus(str, Enum):
    """Lifecycle of a session's persisted transcript (S-0).

    ``RECORDING`` while the session is live and final segments are still being
    appended; ``FINALIZED`` once the session has ended and the transcript is complete
    and safe for the summary feature (S-1) to consume. Stored as its string value.
    """

    RECORDING = "Recording"
    FINALIZED = "Finalized"
