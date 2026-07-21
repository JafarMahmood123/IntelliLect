using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Output;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ClassroomService.Infrastructure.Persistence.Repositories;

public sealed class OutputAdminRepository : IOutputAdminRepository
{
    private const string RecordingType = "Recording";
    private const string SummaryType = "Summary";

    private readonly ApplicationDbContext _context;

    public OutputAdminRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<(List<AdminOutputRow> Items, int TotalCount)> GetOutputsPagedAsync(
        string? search, string? type, string? status, Guid? classroomId, int page, int pageSize, CancellationToken ct = default)
    {
        var wantRecordings = !string.Equals(type, SummaryType, StringComparison.OrdinalIgnoreCase);
        var wantSummaries = !string.Equals(type, RecordingType, StringComparison.OrdinalIgnoreCase);

        // A status string maps onto whichever enum(s) recognize it (e.g. "Available" -> both,
        // "Processing" -> recordings only, "Generating" -> summaries only). If a status is given but
        // does not parse for a type, that type contributes nothing.
        RecordingStatus? recStatus = null;
        SummaryStatus? sumStatus = null;
        var statusGiven = !string.IsNullOrWhiteSpace(status);
        if (statusGiven)
        {
            var trimmed = status!.Trim();
            if (Enum.TryParse<RecordingStatus>(trimmed, ignoreCase: true, out var rs)) recStatus = rs;
            if (Enum.TryParse<SummaryStatus>(trimmed, ignoreCase: true, out var ss)) sumStatus = ss;
            if (recStatus is null) wantRecordings = false;
            if (sumStatus is null) wantSummaries = false;
        }

        var term = string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim()}%";
        var fetch = page * pageSize; // enough of each source to satisfy a merged page at this depth

        var totalCount = 0;
        var rows = new List<AdminOutputRow>();

        if (wantRecordings)
        {
            var query = _context.SessionRecordings.AsNoTracking();
            if (classroomId.HasValue && classroomId.Value != Guid.Empty)
            {
                query = query.Where(r => r.ClassroomId == classroomId.Value);
            }
            if (recStatus.HasValue)
            {
                query = query.Where(r => r.Status == recStatus.Value);
            }
            if (term is not null)
            {
                query = query.Where(r => _context.Sessions.Any(s => s.Id == r.SessionId && EF.Functions.ILike(s.Title, term)));
            }

            totalCount += await query.CountAsync(ct);

            var recs = await query
                .OrderByDescending(r => r.CreatedAtUtc)
                .Take(fetch)
                .Select(r => new Projection
                {
                    Id = r.Id,
                    SessionId = r.SessionId,
                    ClassroomId = r.ClassroomId,
                    SizeBytes = r.SizeBytes,
                    CreatedAtUtc = r.CreatedAtUtc,
                    RecordingStatus = r.Status,
                    SessionTitle = _context.Sessions.Where(s => s.Id == r.SessionId).Select(s => s.Title).FirstOrDefault(),
                    ClassName = _context.Set<Classroom>().Where(c => c.Id == r.ClassroomId).Select(c => c.Name).FirstOrDefault(),
                })
                .ToListAsync(ct);

            rows.AddRange(recs.Select(r => new AdminOutputRow(
                r.Id, RecordingType, r.SessionId, r.SessionTitle ?? string.Empty, r.ClassroomId,
                r.ClassName ?? string.Empty, r.RecordingStatus!.Value.ToString(), r.SizeBytes, r.CreatedAtUtc)));
        }

        if (wantSummaries)
        {
            var query = _context.SessionSummaries.AsNoTracking();
            if (classroomId.HasValue && classroomId.Value != Guid.Empty)
            {
                query = query.Where(s => s.ClassroomId == classroomId.Value);
            }
            if (sumStatus.HasValue)
            {
                query = query.Where(s => s.Status == sumStatus.Value);
            }
            if (term is not null)
            {
                query = query.Where(s => _context.Sessions.Any(ss => ss.Id == s.SessionId && EF.Functions.ILike(ss.Title, term)));
            }

            totalCount += await query.CountAsync(ct);

            var sums = await query
                .OrderByDescending(s => s.CreatedAtUtc)
                .Take(fetch)
                .Select(s => new Projection
                {
                    Id = s.Id,
                    SessionId = s.SessionId,
                    ClassroomId = s.ClassroomId,
                    SizeBytes = 0L, // summaries carry no recorded size
                    CreatedAtUtc = s.CreatedAtUtc,
                    SummaryStatus = s.Status,
                    SessionTitle = _context.Sessions.Where(x => x.Id == s.SessionId).Select(x => x.Title).FirstOrDefault(),
                    ClassName = _context.Set<Classroom>().Where(c => c.Id == s.ClassroomId).Select(c => c.Name).FirstOrDefault(),
                })
                .ToListAsync(ct);

            rows.AddRange(sums.Select(s => new AdminOutputRow(
                s.Id, SummaryType, s.SessionId, s.SessionTitle ?? string.Empty, s.ClassroomId,
                s.ClassName ?? string.Empty, s.SummaryStatus!.Value.ToString(), s.SizeBytes, s.CreatedAtUtc)));
        }

        // Merge the two newest-first sources into one global page.
        var items = rows
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenBy(r => r.OutputId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return (items, totalCount);
    }

    public async Task<SessionRecording?> GetRecordingAsync(Guid recordingId, CancellationToken ct = default)
        => await _context.SessionRecordings.FirstOrDefaultAsync(r => r.Id == recordingId, ct);

    public async Task<SessionSummary?> GetSummaryAsync(Guid summaryId, CancellationToken ct = default)
        => await _context.SessionSummaries.FirstOrDefaultAsync(s => s.Id == summaryId, ct);

    public async Task<bool> IsSessionLiveAsync(Guid sessionId, CancellationToken ct = default)
        => await _context.Sessions.AnyAsync(s => s.Id == sessionId && s.Status == SessionStatus.Live, ct);

    public void RemoveRecording(SessionRecording recording) => _context.SessionRecordings.Remove(recording);
    public void RemoveSummary(SessionSummary summary) => _context.SessionSummaries.Remove(summary);
    public Task SaveChangesAsync(CancellationToken ct = default) => _context.SaveChangesAsync(ct);

    // Intermediate materialization row (enum kept as-is; ToString runs in memory).
    private sealed class Projection
    {
        public Guid Id { get; init; }
        public Guid SessionId { get; init; }
        public Guid ClassroomId { get; init; }
        public long SizeBytes { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public RecordingStatus? RecordingStatus { get; init; }
        public SummaryStatus? SummaryStatus { get; init; }
        public string? SessionTitle { get; init; }
        public string? ClassName { get; init; }
    }
}
