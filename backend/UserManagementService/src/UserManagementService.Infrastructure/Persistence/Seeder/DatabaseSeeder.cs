using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UserManagementService.Application.Abstractions;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Infrastructure.Persistence.Seeder;

public static class DatabaseSeeder
{
    public static async Task SeedAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IHasher>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ApplicationDbContext>>();

        try
        {
            // 1. Seed Roles
            if (!await context.Roles.AnyAsync())
            {
                logger.LogInformation("Roles table is empty. Seeding Roles...");
                context.Roles.AddRange(
                    Role.Create(RoleName.Admin),
                    Role.Create(RoleName.Teacher),
                    Role.Create(RoleName.Student)
                );
                await context.SaveChangesAsync();
            }

            // 2. Resolve Admin Role
            var adminRole = await context.Roles.FirstOrDefaultAsync(r => r.Name == RoleName.Admin);
            if (adminRole == null)
            {
                logger.LogError("Admin role not found in database. Seeding failed.");
                return;
            }

            // 3. Seed Default Admin User (Check by Email)
            const string adminEmail = "admin@intellilect.com";
            var adminExists = await context.Users.AnyAsync(u => u.Email == adminEmail);

            if (!adminExists)
            {
                logger.LogInformation("Admin user missing. Seeding admin@intellilect.com...");

                var adminUser = new User
                {
                    Id = Guid.NewGuid(),
                    UserName = "admin",
                    Email = adminEmail,
                    FirstName = "System",
                    LastName = "Administrator",
                    PasswordHash = hasher.Hash("Admin123!"),
                    CreatedAtUtc = DateTime.UtcNow,
                    RoleId = adminRole.Id,
                    Bio = "Default system administrator account."
                };

                adminUser.Approve(); // Set status to Active

                await context.Users.AddAsync(adminUser);
                await context.SaveChangesAsync();

                logger.LogInformation("Admin user seeded successfully.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while seeding the database.");
        }
    }
}