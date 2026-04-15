using Microsoft.EntityFrameworkCore;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common.Admins;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Infrastructure.Persistence.Repositories;

public sealed class AdminRepository : IAdminRepository
{
    private readonly ApplicationDbContext _context;

    public AdminRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<(List<User> Items, int TotalCount)> GetAdminsAsync(
        AdminQuerySpecification specification,
        CancellationToken ct = default)
    {
        var query = ApplyFilters(BuildAdminQuery(), specification);
        var totalCount = await query.CountAsync(ct);

        var items = await ApplySorting(query, specification)
            .Skip((specification.Page - 1) * specification.PageSize)
            .Take(specification.PageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task<User?> GetAdminByIdAsync(Guid adminId, CancellationToken ct = default)
    {
        return await BuildAdminQuery().FirstOrDefaultAsync(admin => admin.Id == adminId, ct);
    }

    public async Task<Role?> GetAdminRoleAsync(CancellationToken ct = default)
    {
        return await _context.Roles.FirstOrDefaultAsync(role => role.Name == RoleName.Admin, ct);
    }

    private IQueryable<User> BuildAdminQuery()
    {
        return _context.Users
            .Include(user => user.Role)
            .Where(user => !user.IsDeleted && user.Role.Name == RoleName.Admin);
    }

    private static IQueryable<User> ApplyFilters(
        IQueryable<User> query,
        AdminQuerySpecification specification)
    {
        if (!string.IsNullOrWhiteSpace(specification.UserName))
        {
            query = query.Where(user => EF.Functions.ILike(user.UserName, $"%{specification.UserName}%"));
        }

        if (!string.IsNullOrWhiteSpace(specification.Email))
        {
            query = query.Where(user => EF.Functions.ILike(user.Email, $"%{specification.Email}%"));
        }

        if (!string.IsNullOrWhiteSpace(specification.FirstName))
        {
            query = query.Where(user => EF.Functions.ILike(user.FirstName, $"%{specification.FirstName}%"));
        }

        if (!string.IsNullOrWhiteSpace(specification.LastName))
        {
            query = query.Where(user => EF.Functions.ILike(user.LastName, $"%{specification.LastName}%"));
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

    private static IOrderedQueryable<User> ApplySorting(
        IQueryable<User> query,
        AdminQuerySpecification specification)
    {
        return (specification.SortBy, specification.SortDescending) switch
        {
            (AdminQuerySpecification.UserNameSortField, true) => query.OrderByDescending(user => user.UserName).ThenBy(user => user.Id),
            (AdminQuerySpecification.UserNameSortField, false) => query.OrderBy(user => user.UserName).ThenBy(user => user.Id),
            (AdminQuerySpecification.EmailSortField, true) => query.OrderByDescending(user => user.Email).ThenBy(user => user.Id),
            (AdminQuerySpecification.EmailSortField, false) => query.OrderBy(user => user.Email).ThenBy(user => user.Id),
            (AdminQuerySpecification.FirstNameSortField, true) => query.OrderByDescending(user => user.FirstName).ThenBy(user => user.Id),
            (AdminQuerySpecification.FirstNameSortField, false) => query.OrderBy(user => user.FirstName).ThenBy(user => user.Id),
            (AdminQuerySpecification.LastNameSortField, true) => query.OrderByDescending(user => user.LastName).ThenBy(user => user.Id),
            (AdminQuerySpecification.LastNameSortField, false) => query.OrderBy(user => user.LastName).ThenBy(user => user.Id),
            (AdminQuerySpecification.StatusSortField, true) => query.OrderByDescending(user => user.Status).ThenBy(user => user.Id),
            (AdminQuerySpecification.StatusSortField, false) => query.OrderBy(user => user.Status).ThenBy(user => user.Id),
            (_, false) => query.OrderBy(user => user.CreatedAtUtc).ThenBy(user => user.Id),
            _ => query.OrderByDescending(user => user.CreatedAtUtc).ThenBy(user => user.Id)
        };
    }
}
