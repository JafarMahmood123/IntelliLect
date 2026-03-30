namespace UserManagementService.Domain.Entities;

public sealed class Role
{
    public Guid Id { get; }
    public RoleName Name { get; }

    private Role(Guid id, RoleName name)
    {
        Id = id;
        Name = name;
    }

    public static Role Create(RoleName name) => new(Guid.NewGuid(), name);
}

