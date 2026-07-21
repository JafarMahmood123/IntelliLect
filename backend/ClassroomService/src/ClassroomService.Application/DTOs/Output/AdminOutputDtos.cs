namespace ClassroomService.Application.DTOs.Output;

/// <summary>
/// A session output (a recording or a summary) as seen by the super-admin management view. Both
/// live entirely in ClassroomService, which also owns the session title and classroom name shown
/// here — so no cross-service enrichment is needed. <see cref="SizeBytes"/> is the recording's file
/// size; summaries carry no recorded size and report 0.
/// </summary>
public sealed record AdminOutputRow(
    Guid OutputId,
    string Type,          // "Recording" | "Summary"
    Guid SessionId,
    string SessionTitle,
    Guid ClassroomId,
    string ClassName,
    string Status,
    long SizeBytes,
    DateTime CreatedAtUtc);

public sealed record AdminOutputPage(
    IReadOnlyList<AdminOutputRow> Items,
    int TotalCount,
    int PageNumber,
    int PageSize);

/// <summary>Result of deleting an output (step 8): whether the store object(s) were removed and the row.</summary>
public sealed record OutputDeletionResult(Guid OutputId, string Type, bool StorageDeleted, bool RowDeleted);
