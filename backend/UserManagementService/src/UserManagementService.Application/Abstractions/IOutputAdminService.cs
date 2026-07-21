using UserManagementService.Application.DTOs.Output;

namespace UserManagementService.Application.Abstractions;

/// <summary>
/// Super-admin management of session recordings and summaries (use-case "إدارة تسجيلات المحاضرات
/// وملخّصاتها"). ClassroomService owns both outputs (rows + object-store files + session/classroom
/// names), so this gateway validates the reason and proxies.
/// </summary>
public interface IOutputAdminService
{
    Task<OutputListResult> GetOutputsAsync(SearchOutputsRequest request, CancellationToken ct = default);

    /// <exception cref="ArgumentException">No reason supplied (4أ).</exception>
    /// <exception cref="NotFoundException">The recording does not exist (5أ).</exception>
    Task<OutputDeletionSummary> DeleteRecordingAsync(Guid recordingId, string reason, CancellationToken ct = default);

    /// <exception cref="ArgumentException">No reason supplied (4أ).</exception>
    /// <exception cref="NotFoundException">The summary does not exist (5أ).</exception>
    Task<OutputDeletionSummary> DeleteSummaryAsync(Guid summaryId, string reason, CancellationToken ct = default);
}
