namespace UserManagementService.Domain.Entities;

public sealed class Role
{
    // Add 'private set' so EF Core can write to these
    public Guid Id { get; private set; }
    public RoleName Name { get; private set; }

    // 1. ADD THIS: EF Core needs a parameterless constructor
    private Role() { } 

    private Role(Guid id, RoleName name)
    {
        Id = id;
        Name = name;
    }

    public static Role Create(RoleName name) => new(Guid.NewGuid(), name);
}