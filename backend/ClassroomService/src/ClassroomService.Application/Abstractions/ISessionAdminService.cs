using ClassroomService.Application.DTOs;
using ClassroomService.Application.DTOs.Session;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Platform-admin (super admin) session monitoring and force-end. Owns the session status
/// transition and orchestrates the end path (stream end + summary trigger).
/// </summary>
public interface ISessionAdminService
{
    Task<PagedResult<AdminSessionResponse>> GetSessionsAsync(
        int page, int pageSize, string? search, SessionStatus? status, Guid? classroomId, CancellationToken ct = default);

    /// <exception cref="KeyNotFoundException">The session does not exist (6أ).</exception>
    Task<ForceEndSessionResponse> ForceEndAsync(Guid sessionId, string reason, CancellationToken ct = default);
}
