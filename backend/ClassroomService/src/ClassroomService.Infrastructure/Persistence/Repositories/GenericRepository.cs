using Microsoft.EntityFrameworkCore;
using ClassroomService.Application.Abstractions;

namespace ClassroomService.Infrastructure.Persistence.Repositories;

public class GenericRepository<T> : IRepository<T> where T : class
{
    protected readonly DbContext _context;

    public GenericRepository(DbContext context) => _context = context;

    public async Task<T?> GetByIdAsync(Guid id, CancellationToken ct) =>
        await _context.Set<T>().FindAsync(new object[] { id }, ct);

    public async Task<(IEnumerable<T> Items, int TotalCount)> GetPagedAsync(int page, int pageSize, CancellationToken ct)
    {
        var query = _context.Set<T>().AsNoTracking();

        var totalCount = await query.CountAsync(ct);

        // OFFSET = (PageNumber - 1) * PageSize
        // Example: Page 2, Size 10 -> Skip(10).Take(10)
        var items = await query
            .OrderBy(x => x) // EF requires an order for Skip/Take
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task AddAsync(T entity, CancellationToken ct) =>
        await _context.Set<T>().AddAsync(entity, ct);

    public Task UpdateAsync(T entity, CancellationToken ct)
    {
        _context.Set<T>().Update(entity);
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var entity = await GetByIdAsync(id, ct);
        if (entity != null) _context.Set<T>().Remove(entity);
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct) =>
        await _context.SaveChangesAsync(ct);
}