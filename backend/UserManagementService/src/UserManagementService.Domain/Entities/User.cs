namespace UserManagementService.Domain.Entities;

public sealed class User
{
    public Guid Id { get; }
    public string UserName { get; private set; }
    public string Email { get; private set; }
    public string FirstName { get; private set; }
    public string LastName { get; private set; }
    public Guid RoleId { get; private set; }
    public Role Role { get; private set; }

    public DateTime CreatedAtUtc { get; }
    public bool IsActive { get; private set; }

    private User(
        Guid id,
        string userName,
        string email,
        string firstName,
        string lastName,
        Guid roleId,
        DateTime createdAtUtc,
        bool isActive,
        Role role)
    {
        if (string.IsNullOrWhiteSpace(userName))
            throw new ArgumentException("UserName is required.", nameof(userName));
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            throw new ArgumentException("Valid Email is required.", nameof(email));
        if (string.IsNullOrWhiteSpace(firstName))
            throw new ArgumentException("FirstName is required.", nameof(firstName));
        if (string.IsNullOrWhiteSpace(lastName))
            throw new ArgumentException("LastName is required.", nameof(lastName));
        if (roleId == Guid.Empty)
            throw new ArgumentException("RoleId is required.", nameof(roleId));

        Id = id;
        UserName = userName;
        Email = email;
        FirstName = firstName;
        LastName = lastName;
        RoleId = roleId;
        CreatedAtUtc = createdAtUtc;
        IsActive = isActive;
        Role = role;
    }

    public static User Create(string userName, string email, string firstName, string lastName, Guid roleId, Role role)
        => new(
            id: Guid.NewGuid(),
            userName: userName,
            email: email,
            firstName: firstName,
            lastName: lastName,
            roleId: roleId,
            createdAtUtc: DateTime.UtcNow,
            isActive: true,
            role: role);

    public void ChangeRole(Guid roleId, Role role)
    {
        if (roleId == Guid.Empty)
            throw new ArgumentException("RoleId is required.", nameof(roleId));

        RoleId = roleId;
        Role = role;
    }
    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}

