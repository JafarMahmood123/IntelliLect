using ClassroomService.Application.DTOs.Session;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Abstractions;

public interface ISessionRepository
{
    Task<Session?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IEnumerable<Session>> GetByClassroomIdAsync(Guid classroomId, CancellationToken ct = default);
    Task AddAsync(Session session, CancellationToken ct = default);
    Task UpdateAsync(Session session, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);

    // Cross-classroom admin listing: joins the classroom (name/teacher) and the recording/summary
    // status, with optional free-text (title) search and status/classroom filters.
    Task<(List<AdminSessionResponse> Items, int TotalCount)> GetAdminSessionsPagedAsync(
        int page, int pageSize, string? search, SessionStatus? status, Guid? classroomId, CancellationToken ct = default);
}