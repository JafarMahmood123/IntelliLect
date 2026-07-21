namespace UserManagementService.Application.DTOs.Output;

/// <summary>Query parameters for the super-admin recordings/summaries listing.</summary>
public sealed class SearchOutputsRequest
{
    public string? Search { get; set; }
    /// <summary>"Recording" | "Summary" (empty = both).</summary>
    public string? Type { get; set; }
    public string? Status { get; set; }
    public Guid? ClassroomId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

/// <summary>A recording or summary in the super-admin management view.</summary>
public sealed record OutputItem(
    Guid OutputId,
    string Type,
    Guid SessionId,
    string SessionTitle,
    Guid ClassroomId,
    string ClassName,
    string Status,
    long SizeBytes,
    DateTime CreatedAtUtc);

public sealed record OutputListResult(
    IReadOnlyList<OutputItem> Items,
    int TotalCount,
    int PageNumber,
    int PageSize);

/// <summary>Body for deleting an output; the reason is mandatory (4أ).</summary>
public sealed record DeleteOutputRequest(string Reason);

/// <summary>Result of a completed output deletion (step 8).</summary>
public sealed record OutputDeletionSummary(Guid OutputId, string Type, bool StorageDeleted, bool RowDeleted);
