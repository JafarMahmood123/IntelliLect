from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field


class IngestDocumentRequest(BaseModel):
    """Inbound payload for the internal ingest endpoint.

    Uses camelCase aliases to match the .NET services that call it while keeping
    snake_case attribute names on the Python side.
    """

    model_config = ConfigDict(populate_by_name=True)

    file_id: UUID = Field(alias="fileId")
    classroom_id: UUID = Field(alias="classroomId")
    s3_key: str = Field(alias="s3Key")
    file_name: str = Field(alias="fileName")
    content_type: str = Field(alias="contentType")
    # Optional for backward compatibility with callers that don't send it yet.
    size_bytes: int = Field(default=0, alias="sizeBytes")


class IngestDocumentResponse(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    document_id: UUID = Field(alias="documentId")
    file_id: UUID = Field(alias="fileId")
    status: str


class DeleteClassroomIndexResponse(BaseModel):
    """Result of de-indexing a whole classroom.

    Counts let the caller record how much was removed and, on a retry, see that a
    second pass removed nothing because the first already succeeded.
    """

    model_config = ConfigDict(populate_by_name=True)

    classroom_id: UUID = Field(alias="classroomId")
    documents_deleted: int = Field(alias="documentsDeleted")
    chunks_deleted: int = Field(alias="chunksDeleted")


class DocumentStatusResponse(BaseModel):
    """Read model for a document's indexing status.

    Consumed server-side by ClassroomService (which re-authorizes by membership
    and forwards a trimmed view to the browser). Deliberately exposes only the
    file id and lifecycle status — never s3 keys, error detail, or chunk data.
    """

    model_config = ConfigDict(populate_by_name=True)

    file_id: UUID = Field(alias="fileId")
    status: str


# --- Super-admin knowledge-base management ---


class AdminDocumentItem(BaseModel):
    """One row in the super-admin document list / status-batch result."""

    model_config = ConfigDict(populate_by_name=True)

    file_id: UUID = Field(alias="fileId")
    classroom_id: UUID = Field(alias="classroomId")
    file_name: str = Field(alias="fileName")
    content_type: str = Field(alias="contentType")
    size_bytes: int = Field(alias="sizeBytes")
    status: str
    attempts: int
    chunk_count: int = Field(alias="chunkCount")


class AdminDocumentListResponse(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    items: list[AdminDocumentItem]
    total: int
    page: int
    page_size: int = Field(alias="pageSize")


class DocumentDetailResponse(BaseModel):
    """A single document's diagnostics (step 4): status, attempts and the failure reason."""

    model_config = ConfigDict(populate_by_name=True)

    file_id: UUID = Field(alias="fileId")
    classroom_id: UUID = Field(alias="classroomId")
    file_name: str = Field(alias="fileName")
    content_type: str = Field(alias="contentType")
    size_bytes: int = Field(alias="sizeBytes")
    status: str
    attempts: int
    chunk_count: int = Field(alias="chunkCount")
    last_error: str | None = Field(default=None, alias="lastError")


class StatusBatchRequest(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    file_ids: list[UUID] = Field(alias="fileIds")


class KnowledgeStatsResponse(BaseModel):
    """Knowledge-base statistics (step 5) for a classroom or the whole platform."""

    model_config = ConfigDict(populate_by_name=True)

    classroom_id: UUID | None = Field(default=None, alias="classroomId")
    document_count: int = Field(alias="documentCount")
    status_counts: dict[str, int] = Field(alias="statusCounts")
    total_chunks: int = Field(alias="totalChunks")
    failed_count: int = Field(alias="failedCount")
    storage_bytes: int = Field(alias="storageBytes")


class BulkReindexResponse(BaseModel):
    """Result of a classroom bulk reindex (step 7 / 7د): how many were enqueued vs skipped."""

    model_config = ConfigDict(populate_by_name=True)

    classroom_id: UUID = Field(alias="classroomId")
    requested: int
    enqueued: int
    skipped: int
