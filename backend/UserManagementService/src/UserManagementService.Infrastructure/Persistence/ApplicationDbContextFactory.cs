using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace UserManagementService.Infrastructure.Persistence;

public sealed class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        var apiProjectDirectory = ResolveApiProjectDirectory(currentDirectory);

        var configuration = new ConfigurationBuilder()
            .SetBasePath(apiProjectDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("Database")
            ?? throw new InvalidOperationException("Connection string 'Database' was not found.");

        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder.UseNpgsql(connectionString);

        return new ApplicationDbContext(optionsBuilder.Options);
    }

    private static string ResolveApiProjectDirectory(string currentDirectory)
    {
        var localPath = Path.Combine(currentDirectory, "UserManagementService.Api");
        if (Directory.Exists(localPath))
        {
            return localPath;
        }

        var siblingPath = Path.Combine(currentDirectory, "..", "UserManagementService.Api");
        if (Directory.Exists(siblingPath))
        {
            return Path.GetFullPath(siblingPath);
        }

        throw new DirectoryNotFoundException("Could not locate the UserManagementService.Api project directory.");
    }
}
