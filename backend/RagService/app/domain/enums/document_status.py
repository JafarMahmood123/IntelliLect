from enum import Enum


class DocumentStatus(str, Enum):
    """Lifecycle state of an ingested document."""

    PENDING = "Pending"
    PROCESSING = "Processing"
    DONE = "Done"
    FAILED = "Failed"
