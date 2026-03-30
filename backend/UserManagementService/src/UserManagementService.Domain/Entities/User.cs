namespace UserManagementService.Domain.Entities;

public sealed class User
{
    public Guid Id { get; private set; } // Add private set
    public string UserName { get; private set; }
    public string Email { get; private set; }
    public string FirstName { get; private set; }
    public string LastName { get; private set; }
    public Guid RoleId { get; private set; }
    public Role Role { get; private set; }
    public string PasswordHash { get; private set; }

    public DateTime CreatedAtUtc { get; private set; } // Add private set
    public bool IsActive { get; private set; }

    private User() { }


    private User(
        Guid id,
        string userName,
        string email,
        string firstName,
        string lastName,
        Guid roleId,
        string passwordHash,
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
        if (string.IsNullOrWhiteSpace(passwordHash))
            throw new ArgumentException("PasswordHash is required.", nameof(passwordHash));

        Id = id;
        UserName = userName;
        Email = email;
        FirstName = firstName;
        LastName = lastName;
        RoleId = roleId;
        PasswordHash = passwordHash;
        CreatedAtUtc = createdAtUtc;
        IsActive = isActive;
        Role = role;
    }

    public static User Create(
        string userName,
        string email,
        string firstName,
        string lastName,
        Guid roleId,
        string passwordHash,
        Role role)
        => new(
            id: Guid.NewGuid(),
            userName: userName,
            email: email,
            firstName: firstName,
            lastName: lastName,
            roleId: roleId,
            passwordHash: passwordHash,
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

    public void ChangePasswordHash(string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
            throw new ArgumentException("PasswordHash is required.", nameof(passwordHash));

        PasswordHash = passwordHash;
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}

