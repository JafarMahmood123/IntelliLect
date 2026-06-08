namespace UserManagementService.Domain.Entities;

public sealed class Role
{
    // Main Properties
    public Guid Id { get; set; }
    public RoleName Name { get; set; }

    // Foreign Keys

    // Navigation Properties
    public ICollection<User> Users { get; set; } = new List<User>();

    public Role() { }

    public static Role Create(RoleName name)
    {
        return new Role
        {
            Id = Guid.NewGuid(),
            Name = name
        };
    }
}