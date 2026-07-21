using ClassroomService.Application.Abstractions;
using ClassroomService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassroomService.Infrastructure.Persistence.Repositories;

public sealed class SessionDeletionRepository : ISessionDeletionRepository
{
    private readonly ApplicationDbContext _context;

    public SessionDeletionRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Session?> GetTrackedAsync(Guid sessionId, CancellationToken ct = default)
        => await _context.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);

    public async Task<SessionRecording?> GetRecordingAsync(Guid sessionId, CancellationToken ct = default)
        => await _context.SessionRecordings.FirstOrDefaultAsync(r => r.SessionId == sessionId, ct);

    public async Task<SessionSummary?> GetSummaryAsync(Guid sessionId, CancellationToken ct = default)
        => await _context.SessionSummaries.FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);

    public void RemoveRecording(SessionRecording recording) => _context.SessionRecordings.Remove(recording);
    public void RemoveSummary(SessionSummary summary) => _context.SessionSummaries.Remove(summary);
    public void RemoveSession(Session session) => _context.Sessions.Remove(session);

    public Task SaveChangesAsync(CancellationToken ct = default) => _context.SaveChangesAsync(ct);
}
