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
            await EnsureRoleExistsAsync(context, logger, RoleName.Admin);
            await EnsureRoleExistsAsync(context, logger, RoleName.Teacher);
            await EnsureRoleExistsAsync(context, logger, RoleName.Student);
            await EnsureRoleExistsAsync(context, logger, RoleName.SuperAdmin);

            var adminRole = await context.Roles.FirstOrDefaultAsync(r => r.Name == RoleName.Admin);
            if (adminRole == null)
            {
                logger.LogError("Admin role not found in database. Seeding failed.");
                return;
            }

            var superAdminRole = await context.Roles.FirstOrDefaultAsync(r => r.Name == RoleName.SuperAdmin);
            if (superAdminRole == null)
            {
                logger.LogError("SuperAdmin role not found in database. Seeding failed.");
                return;
            }

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

            const string superAdminEmail = "superadmin@intellilect.com";
            var superAdminExists = await context.Users.AnyAsync(u => u.Email == superAdminEmail);

            if (!superAdminExists)
            {
                logger.LogInformation("Super admin user missing. Seeding superadmin@intellilect.com...");

                var superAdminUser = new User
                {
                    Id = Guid.NewGuid(),
                    UserName = "superadmin",
                    Email = superAdminEmail,
                    FirstName = "System",
                    LastName = "Super Administrator",
                    PasswordHash = hasher.Hash("SuperAdmin123!"),
                    CreatedAtUtc = DateTime.UtcNow,
                    RoleId = superAdminRole.Id,
                    Bio = "Default system super administrator account."
                };

                superAdminUser.Approve();

                await context.Users.AddAsync(superAdminUser);
                await context.SaveChangesAsync();

                logger.LogInformation("Super admin user seeded successfully.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while seeding the database.");
        }
    }

    private static async Task EnsureRoleExistsAsync(
        ApplicationDbContext context,
        ILogger logger,
        RoleName roleName)
    {
        var exists = await context.Roles.AnyAsync(r => r.Name == roleName);
        if (exists)
        {
            return;
        }

        logger.LogInformation("Role {RoleName} is missing. Seeding it now...", roleName);
        context.Roles.Add(Role.Create(roleName));
        await context.SaveChangesAsync();
    }
}
