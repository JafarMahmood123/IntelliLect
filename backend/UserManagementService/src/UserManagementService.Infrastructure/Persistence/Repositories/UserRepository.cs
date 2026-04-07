using Microsoft.EntityFrameworkCore;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Infrastructure.Persistence.Repositories;

public sealed class UserRepository : IUserRepository
{
    private readonly ApplicationDbContext _context;

    public UserRepository(ApplicationDbContext context) => _context = context;

    public async Task<User?> GetByIdAsync(Guid id, CancellationToken ct) =>
        await _context.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == id, ct);

    public async Task<User?> FindByEmail(string email, CancellationToken ct) =>
        await _context.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Email == email, ct);

    public async Task AddAsync(User user, CancellationToken ct) => await _context.Users.AddAsync(user, ct);

    public Task UpdateAsync(User user, CancellationToken ct)
    {
        _context.Users.Update(user);
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var user = await _context.Users.FindAsync(new object[] { id }, ct);
        if (user != null)
        {
            user.SoftDelete();
            _context.Users.Update(user);
        }
    }

    public async Task<User?> FindByRefreshToken(string token, CancellationToken ct)
    {
        return await _context.Users
            .Include(u => u.Role)
            .Include(u => u.RefreshTokens)
            .FirstOrDefaultAsync(u => u.RefreshTokens.Any(rt => rt.Token == token && !rt.IsRevoked), ct);
    }

    public async Task<User?> FindByResetToken(string token, CancellationToken ct)
    {
        return await _context.Users
            .Include(u => u.Role)
            .Include(u => u.ResetPasswordToken)
            .FirstOrDefaultAsync(u => u.ResetPasswordToken != null && u.ResetPasswordToken.Token == token, ct);
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        return await _context.SaveChangesAsync(ct);
    }

    public async Task<(List<User> Items, int TotalCount)> GetPaginatedPendingUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct)
    {
        var query = _context.Users
            .Include(u => u.Role)
            .Where(u => u.Status == UserStatus.Pending && !u.IsDeleted);

        if (roleId.HasValue && roleId.Value != Guid.Empty)
        {
            query = query.Where(u => u.RoleId == roleId.Value);
        }

        int totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(u => u.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task<(List<User> Items, int TotalCount)> GetPaginatedUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct)
    {
        var query = _context.Users.Include(u => u.Role).AsQueryable();

        if (roleId.HasValue && roleId.Value != Guid.Empty)
        {
            query = query.Where(u => u.RoleId == roleId.Value);
        }

        int totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(u => u.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }
}