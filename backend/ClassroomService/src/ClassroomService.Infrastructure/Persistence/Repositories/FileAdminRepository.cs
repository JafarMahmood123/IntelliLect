using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.File;
using ClassroomService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassroomService.Infrastructure.Persistence.Repositories;

public sealed class FileAdminRepository : IFileAdminRepository
{
    private readonly ApplicationDbContext _context;

    public FileAdminRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<(List<AdminFileRow> Items, int TotalCount)> GetPagedAsync(
        string? search, Guid? classroomId, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _context.ClassroomFiles.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(f => EF.Functions.ILike(f.FileName, term));
        }
        if (classroomId.HasValue && classroomId.Value != Guid.Empty)
        {
            query = query.Where(f => f.ClassroomId == classroomId.Value);
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(f => f.FileName)
            .ThenBy(f => f.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(f => new AdminFileRow(f.Id, f.FileName, f.ContentType, f.SizeBytes, f.ClassroomId))
            .ToListAsync(ct);

        return (items, total);
    }

    public async Task<List<AdminFileRow>> GetByIdsAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken ct = default)
    {
        if (fileIds.Count == 0)
        {
            return new List<AdminFileRow>();
        }

        return await _context.ClassroomFiles
            .AsNoTracking()
            .Where(f => fileIds.Contains(f.Id))
            .Select(f => new AdminFileRow(f.Id, f.FileName, f.ContentType, f.SizeBytes, f.ClassroomId))
            .ToListAsync(ct);
    }

    public async Task<ClassroomFile?> GetByIdAsync(Guid fileId, CancellationToken ct = default)
        => await _context.ClassroomFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);

    public void Remove(ClassroomFile file) => _context.ClassroomFiles.Remove(file);

    public Task SaveChangesAsync(CancellationToken ct = default) => _context.SaveChangesAsync(ct);
}
