using Microsoft.EntityFrameworkCore;
using UserManagementService.Application.Abstractions;
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
        var user = await GetByIdAsync(id, ct);
        if (user != null) _context.Users.Remove(user);
    }
}