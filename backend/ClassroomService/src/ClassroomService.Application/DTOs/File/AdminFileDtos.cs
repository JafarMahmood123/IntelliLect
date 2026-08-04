namespace ClassroomService.Application.DTOs.File;

/// <summary>
/// A file as seen by the super-admin knowledge-base view: the authoritative registry fields owned by
/// ClassroomService (name, size, classroom). Indexing status / chunk count are enriched by the caller
/// from RagService — they are not part of this row.
/// </summary>
public sealed record AdminFileRow(
    Guid FileId,
    string FileName,
    string ContentType,
    long SizeBytes,
    Guid ClassroomId);

/// <summary>A page of admin files.</summary>
public sealed record AdminFilePage(
    IReadOnlyList<AdminFileRow> Items,
    int TotalCount,
    int PageNumber,
    int PageSize);

/// <summary>Result of a super-admin file deletion (step 7/7هـ).</summary>
public sealed record AdminFileDeletionResult(Guid FileId, bool StorageDeleted, bool DeIndexed);
