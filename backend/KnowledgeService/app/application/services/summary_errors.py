from __future__ import annotations

import httpx


class SummaryError(Exception):
    """Base class for summary failures with a known transient/permanent nature."""


class TransientSummaryError(SummaryError):
    """Worth retrying: rate limit, 5xx, timeout, transcript not ready yet."""


class PermanentSummaryError(SummaryError):
    """Will not succeed on retry: no transcript exists, transcript is empty, prompt blocked."""


# Substrings that identify a permanent failure in an error message. Matching on text is
# unpleasant, but the alternative is threading typed errors through three ports (transcript
# client, generation provider, storage) that today raise their own vocabularies. These are the
# cases where retrying is guaranteed to waste an LLM call, so a false NEGATIVE (retrying
# something permanent) merely costs attempts, while a false positive would abandon a recoverable
# summary. The list is therefore deliberately narrow.
_PERMANENT_MARKERS = (
    "no transcript for session",  # TranscriptFetchError 404 — the session was never transcribed
    "blockreason",  # Gemini safety block; the same prompt will be blocked again
)


def is_transient(exc: BaseException) -> bool:
    """Classify a summary failure as transient (retry) or permanent (fail fast).

    Explicitly-typed summary errors win. Then HTTP status: 429 and 5xx are transient, other 4xx
    are not (a bad key or a missing model will not fix itself). Everything else defaults to
    TRANSIENT, matching ingestion's stance — an unclassified failure is more often an
    infrastructure hiccup than a permanent one, and the attempt budget bounds the cost of being
    wrong.
    """
    if isinstance(exc, PermanentSummaryError):
        return False
    if isinstance(exc, TransientSummaryError):
        return True

    if isinstance(exc, httpx.HTTPStatusError):
        status = exc.response.status_code
        if status == 429 or status >= 500:
            return True
        if 400 <= status < 500:
            return False

    message = str(exc).lower()
    if any(marker in message for marker in _PERMANENT_MARKERS):
        return False
    return True
