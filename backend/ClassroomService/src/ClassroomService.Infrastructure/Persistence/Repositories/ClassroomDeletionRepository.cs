using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Classroom;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ClassroomService.Infrastructure.Persistence.Repositories;

public sealed class ClassroomDeletionRepository : IClassroomDeletionRepository
{
    private readonly ApplicationDbContext _context;

    public ClassroomDeletionRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ClassroomDeletionImpact?> GetImpactAsync(Guid classroomId, CancellationToken ct = default)
    {
        var classroom = await _context.Classrooms
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == classroomId, ct);

        if (classroom is null)
        {
            return null;
        }

        var sessionCount = await _context.Sessions.CountAsync(s => s.ClassroomId == classroomId, ct);
        var memberCount = await _context.ClassroomMemberships.CountAsync(m => m.ClassroomId == classroomId, ct);
        var recordingCount = await _context.SessionRecordings.CountAsync(r => r.ClassroomId == classroomId, ct);
        var summaryCount = await _context.SessionSummaries.CountAsync(s => s.ClassroomId == classroomId, ct);

        // Storage that will be freed: classroom files + session recordings. Summaries carry no size.
        var fileBytes = await _context.ClassroomFiles
            .Where(f => f.ClassroomId == classroomId)
            .SumAsync(f => (long?)f.SizeBytes, ct) ?? 0L;
        var fileCount = await _context.ClassroomFiles.CountAsync(f => f.ClassroomId == classroomId, ct);
        var recordingBytes = await _context.SessionRecordings
            .Where(r => r.ClassroomId == classroomId)
            .SumAsync(r => (long?)r.SizeBytes, ct) ?? 0L;

        var hasLiveSession = await _context.Sessions
            .AnyAsync(s => s.ClassroomId == classroomId && s.Status == SessionStatus.Live, ct);

        return new ClassroomDeletionImpact(
            classroom.Id,
            classroom.Name,
            classroom.Status == ClassroomStatus.PendingDeletion ? "PendingDeletion" : "Active",
            sessionCount,
            memberCount,
            fileCount,
            recordingCount,
            summaryCount,
            fileBytes + recordingBytes,
            hasLiveSession);
    }

    public async Task<Classroom?> GetTrackedAsync(Guid classroomId, CancellationToken ct = default)
    {
        return await _context.Classrooms.FirstOrDefaultAsync(c => c.Id == classroomId, ct);
    }

    public async Task<bool> HasLiveSessionAsync(Guid classroomId, CancellationToken ct = default)
    {
        return await _context.Sessions
            .AnyAsync(s => s.ClassroomId == classroomId && s.Status == SessionStatus.Live, ct);
    }

    public async Task<List<SessionRecording>> GetRecordingsAsync(Guid classroomId, CancellationToken ct = default)
    {
        return await _context.SessionRecordings
            .Where(r => r.ClassroomId == classroomId)
            .ToListAsync(ct);
    }

    public async Task<List<SessionSummary>> GetSummariesAsync(Guid classroomId, CancellationToken ct = default)
    {
        return await _context.SessionSummaries
            .Where(s => s.ClassroomId == classroomId)
            .ToListAsync(ct);
    }

    public async Task<List<ClassroomFile>> GetFilesAsync(Guid classroomId, CancellationToken ct = default)
    {
        return await _context.ClassroomFiles
            .Where(f => f.ClassroomId == classroomId)
            .ToListAsync(ct);
    }

    public void RemoveRecording(SessionRecording recording) => _context.SessionRecordings.Remove(recording);
    public void RemoveSummary(SessionSummary summary) => _context.SessionSummaries.Remove(summary);
    public void RemoveFile(ClassroomFile file) => _context.ClassroomFiles.Remove(file);
    public void RemoveClassroom(Classroom classroom) => _context.Classrooms.Remove(classroom);

    public async Task<int> DeleteSessionsAsync(Guid classroomId, CancellationToken ct = default)
    {
        return await _context.Sessions
            .Where(s => s.ClassroomId == classroomId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<int> DeleteMembershipsAsync(Guid classroomId, CancellationToken ct = default)
    {
        return await _context.ClassroomMemberships
            .Where(m => m.ClassroomId == classroomId)
            .ExecuteDeleteAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => _context.SaveChangesAsync(ct);
}
