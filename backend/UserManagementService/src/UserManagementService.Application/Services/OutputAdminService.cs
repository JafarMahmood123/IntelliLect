using UserManagementService.Application.Abstractions;
using UserManagementService.Application.DTOs.Output;

namespace UserManagementService.Application.OutputAdministration;

public sealed class OutputAdminService : IOutputAdminService
{
    private readonly IClassroomInternalClient _classroomClient;

    public OutputAdminService(IClassroomInternalClient classroomClient)
    {
        _classroomClient = classroomClient;
    }

    public async Task<OutputListResult> GetOutputsAsync(SearchOutputsRequest request, CancellationToken ct = default)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = Math.Clamp(request.PageSize < 1 ? 20 : request.PageSize, 1, 100);

        var result = await _classroomClient.GetOutputsAsync(
            page, pageSize, request.Search, request.Type, request.Status, request.ClassroomId, ct);

        var items = result.Items.Select(o => new OutputItem(
            o.OutputId, o.Type, o.SessionId, o.SessionTitle, o.ClassroomId, o.ClassName,
            o.Status, o.SizeBytes, o.CreatedAtUtc)).ToList();

        return new OutputListResult(items, result.TotalCount, page, pageSize);
    }

    public async Task<OutputDeletionSummary> DeleteRecordingAsync(Guid recordingId, string reason, CancellationToken ct = default)
    {
        var trimmed = RequireReason(reason);
        // NotFoundException (5أ) / InvalidOperationException (5ب, live session) propagate from the client.
        var result = await _classroomClient.DeleteRecordingAsync(recordingId, trimmed, ct);
        return new OutputDeletionSummary(result.OutputId, result.Type, result.StorageDeleted, result.RowDeleted);
    }

    public async Task<OutputDeletionSummary> DeleteSummaryAsync(Guid summaryId, string reason, CancellationToken ct = default)
    {
        var trimmed = RequireReason(reason);
        var result = await _classroomClient.DeleteSummaryAsync(summaryId, trimmed, ct);
        return new OutputDeletionSummary(result.OutputId, result.Type, result.StorageDeleted, result.RowDeleted);
    }

    private static string RequireReason(string? reason)
    {
        // 4أ: a reason is mandatory before any cross-service call.
        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("A deletion reason is required.");
        }
        return trimmed;
    }
}
