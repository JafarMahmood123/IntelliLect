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


class IngestDocumentResponse(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    document_id: UUID = Field(alias="documentId")
    file_id: UUID = Field(alias="fileId")
    status: str


class DocumentStatusResponse(BaseModel):
    """Read model for a document's indexing status.

    Consumed server-side by ClassroomService (which re-authorizes by membership
    and forwards a trimmed view to the browser). Deliberately exposes only the
    file id and lifecycle status — never s3 keys, error detail, or chunk data.
    """

    model_config = ConfigDict(populate_by_name=True)

    file_id: UUID = Field(alias="fileId")
    status: str
