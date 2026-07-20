using Microsoft.EntityFrameworkCore;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;
using UserManagementService.Application.Common.Users;
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
            _context.Users.Remove(user);
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
            .Where(u => u.Status == UserStatus.Pending);

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

    public async Task<(List<User> Items, int TotalCount)> SearchUsersAsync(UserQuerySpecification specification, CancellationToken ct = default)
    {
        var query = ApplyUserFilters(_context.Users.Include(u => u.Role), specification);

        int totalCount = await query.CountAsync(ct);

        var items = await ApplyUserSorting(query, specification)
            .Skip((specification.Page - 1) * specification.PageSize)
            .Take(specification.PageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    private static IQueryable<User> ApplyUserFilters(IQueryable<User> query, UserQuerySpecification specification)
    {
        if (!string.IsNullOrWhiteSpace(specification.SearchTerm))
        {
            // Free-text search across the human-facing identifiers (case-insensitive).
            var term = $"%{specification.SearchTerm}%";
            query = query.Where(user =>
                EF.Functions.ILike(user.UserName, term) ||
                EF.Functions.ILike(user.Email, term) ||
                EF.Functions.ILike(user.FirstName, term) ||
                EF.Functions.ILike(user.LastName, term));
        }

        if (specification.Role.HasValue)
        {
            query = query.Where(user => user.Role.Name == specification.Role.Value);
        }

        if (specification.Status.HasValue)
        {
            query = query.Where(user => user.Status == specification.Status.Value);
        }

        if (specification.CreatedFrom.HasValue)
        {
            query = query.Where(user => user.CreatedAtUtc >= specification.CreatedFrom.Value);
        }

        if (specification.CreatedTo.HasValue)
        {
            query = query.Where(user => user.CreatedAtUtc <= specification.CreatedTo.Value);
        }

        return query;
    }

    private static IOrderedQueryable<User> ApplyUserSorting(IQueryable<User> query, UserQuerySpecification specification)
    {
        return (specification.SortBy, specification.SortDescending) switch
        {
            (UserQuerySpecification.UserNameSortField, true) => query.OrderByDescending(u => u.UserName).ThenBy(u => u.Id),
            (UserQuerySpecification.UserNameSortField, false) => query.OrderBy(u => u.UserName).ThenBy(u => u.Id),
            (UserQuerySpecification.EmailSortField, true) => query.OrderByDescending(u => u.Email).ThenBy(u => u.Id),
            (UserQuerySpecification.EmailSortField, false) => query.OrderBy(u => u.Email).ThenBy(u => u.Id),
            (UserQuerySpecification.FirstNameSortField, true) => query.OrderByDescending(u => u.FirstName).ThenBy(u => u.Id),
            (UserQuerySpecification.FirstNameSortField, false) => query.OrderBy(u => u.FirstName).ThenBy(u => u.Id),
            (UserQuerySpecification.LastNameSortField, true) => query.OrderByDescending(u => u.LastName).ThenBy(u => u.Id),
            (UserQuerySpecification.LastNameSortField, false) => query.OrderBy(u => u.LastName).ThenBy(u => u.Id),
            (UserQuerySpecification.StatusSortField, true) => query.OrderByDescending(u => u.Status).ThenBy(u => u.Id),
            (UserQuerySpecification.StatusSortField, false) => query.OrderBy(u => u.Status).ThenBy(u => u.Id),
            (UserQuerySpecification.RoleSortField, true) => query.OrderByDescending(u => u.Role.Name).ThenBy(u => u.Id),
            (UserQuerySpecification.RoleSortField, false) => query.OrderBy(u => u.Role.Name).ThenBy(u => u.Id),
            (_, false) => query.OrderBy(u => u.CreatedAtUtc).ThenBy(u => u.Id),
            _ => query.OrderByDescending(u => u.CreatedAtUtc).ThenBy(u => u.Id)
        };
    }
}
