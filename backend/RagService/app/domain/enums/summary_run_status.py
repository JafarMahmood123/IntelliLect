from enum import Enum


class SummaryRunStatus(str, Enum):
    """Lifecycle state of one session's summary generation.

    Mirrors ``DocumentStatus``, because summaries now use the same machinery ingestion does:
    an atomic claim, an attempt counter, transient-vs-permanent classification, and a stale
    sweep. PascalCase values match the .NET side's ``SummaryStatus``.

    PENDING is the only state a claim can move out of, so it is also what the stale sweep
    resets a lost RUNNING row back to. DONE and FAILED are terminal for the automatic path —
    only a manual regeneration request re-opens them.
    """

    PENDING = "Pending"
    RUNNING = "Running"
    DONE = "Done"
    FAILED = "Failed"
