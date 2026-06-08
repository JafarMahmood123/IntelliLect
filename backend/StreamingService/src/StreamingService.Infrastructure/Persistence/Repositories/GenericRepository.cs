using Microsoft.EntityFrameworkCore;
using StreamingService.Application.Abstractions;

namespace StreamingService.Infrastructure.Persistence.Repositories;

public class GenericRepository<T> : IRepository<T> where T : class
{
    protected readonly DbContext _context;

    public GenericRepository(DbContext context) => _context = context;

    public async Task<T?> GetByIdAsync(Guid id, CancellationToken ct) =>
        await _context.Set<T>().FindAsync(new object[] { id }, ct);

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