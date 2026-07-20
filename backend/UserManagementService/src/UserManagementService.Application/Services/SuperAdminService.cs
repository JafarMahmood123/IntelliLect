using AutoMapper;
using IntelliLect.Contracts.Messages;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;
using UserManagementService.Application.Common.Admins;
using UserManagementService.Application.DTOs.Admin;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Application.SuperAdministration;

public sealed class SuperAdminService : ISuperAdminService
{
    private readonly IAdminRepository _adminRepository;
    private readonly IUserRepository _userRepository;
    private readonly IHasher _hasher;
    private readonly IMapper _mapper;
    private readonly IEventBus _eventBus;

    public SuperAdminService(
        IAdminRepository adminRepository,
        IUserRepository userRepository,
        IHasher hasher,
        IMapper mapper,
        IEventBus eventBus)
    {
        _adminRepository = adminRepository;
        _userRepository = userRepository;
        _hasher = hasher;
        _mapper = mapper;
        _eventBus = eventBus;
    }

    public async Task<PagedResult<AdminQueryResult>> GetAdminsAsync(GetAdminsRequest request, CancellationToken ct = default)
    {
        var specification = AdminQuerySpecification.Create(request);
        var (admins, totalCount) = await _adminRepository.GetAdminsAsync(specification, ct);
        var items = _mapper.Map<List<AdminQueryResult>>(admins);

        return new PagedResult<AdminQueryResult>(items, totalCount, specification.Page, specification.PageSize);
    }

    public async Task<GroupedAdminsResponse> GetGroupedAdminsAsync(GetAdminsRequest request, CancellationToken ct = default)
    {
        var specification = AdminQuerySpecification.Create(request);
        var (admins, totalCount) = await _adminRepository.GetAdminsAsync(specification, ct);
        var items = _mapper.Map<List<AdminQueryResult>>(admins);

        var groups = items
            .GroupBy(admin => admin.Status)
            .Select(group => new AdminStatusGroupResult(group.Key, group.ToList()))
            .ToList();

        return new GroupedAdminsResponse(groups, totalCount, specification.Page, specification.PageSize);
    }

    public async Task<PagedResult<AdminQueryResult>> SearchAdminsAsync(SearchAdminsRequest request, CancellationToken ct = default)
    {
        var specification = AdminQuerySpecification.Create(request);
        var (admins, totalCount) = await _adminRepository.GetAdminsAsync(specification, ct);
        var items = _mapper.Map<List<AdminQueryResult>>(admins);

        return new PagedResult<AdminQueryResult>(items, totalCount, specification.Page, specification.PageSize);
    }

    public async Task<Guid> CreateAdminAsync(CreateAdminRequest request, CancellationToken ct = default)
    {
        // Alternate path 5ب: reject missing or malformed input before touching the database.
        ValidateCreateRequest(request);

        // Alternate path 5أ: the email must not already belong to any account.
        var existingUser = await _userRepository.FindByEmail(request.Email, ct);
        if (existingUser != null)
        {
            throw new InvalidOperationException("A user with this email already exists.");
        }

        var adminRole = await _adminRepository.GetAdminRoleAsync(ct);
        if (adminRole == null)
        {
            throw new InvalidOperationException("Admin role is not configured.");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = request.UserName,
            Email = request.Email,
            FirstName = request.FirstName,
            LastName = request.LastName,
            PasswordHash = _hasher.Hash(request.Password),
            CreatedAtUtc = DateTime.UtcNow,
            RoleId = adminRole.Id,
            Role = adminRole,
            Bio = request.Bio
        };

        user.Approve();

        await _userRepository.AddAsync(user, ct);
        await _userRepository.SaveChangesAsync(ct);

        return user.Id;
    }

    public async Task DeactivateAdminAsync(Guid adminId, Guid requestingSuperAdminId, CancellationToken ct = default)
    {
        // Alternate path 5د: the super admin must not deactivate their own account.
        GuardAgainstSelfTarget(adminId, requestingSuperAdminId);

        var admin = await GetAdminOrThrowAsync(adminId, ct);

        admin.Deactivate();
        await _userRepository.UpdateAsync(admin, ct);
        await _eventBus.PublishAsync(new UserStatusChangedMessage(admin.Email, admin.FirstName, UserStatus.Deactivated.ToString()), ct);
        await _userRepository.SaveChangesAsync(ct);
    }

    public async Task ReactivateAdminAsync(Guid adminId, Guid requestingSuperAdminId, CancellationToken ct = default)
    {
        // Alternate path 5د: symmetrical guard for reactivation of the caller's own account.
        GuardAgainstSelfTarget(adminId, requestingSuperAdminId);

        var admin = await GetAdminOrThrowAsync(adminId, ct);

        admin.Reactivate();
        await _userRepository.UpdateAsync(admin, ct);
        await _eventBus.PublishAsync(new UserStatusChangedMessage(admin.Email, admin.FirstName, UserStatus.Active.ToString()), ct);
        await _userRepository.SaveChangesAsync(ct);
    }

    // Alternate path 5ج: a missing target admin is reported as a 404, not a generic error.
    private async Task<User> GetAdminOrThrowAsync(Guid adminId, CancellationToken ct)
    {
        var admin = await _adminRepository.GetAdminByIdAsync(adminId, ct);
        if (admin == null)
        {
            throw new NotFoundException("Admin not found.");
        }

        return admin;
    }

    private static void GuardAgainstSelfTarget(Guid adminId, Guid requestingSuperAdminId)
    {
        if (adminId == requestingSuperAdminId)
        {
            throw new InvalidOperationException("You cannot change the status of your own account.");
        }
    }

    private static void ValidateCreateRequest(CreateAdminRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserName))
            throw new ArgumentException("Username is required.");

        if (string.IsNullOrWhiteSpace(request.FirstName))
            throw new ArgumentException("First name is required.");

        if (string.IsNullOrWhiteSpace(request.LastName))
            throw new ArgumentException("Last name is required.");

        if (string.IsNullOrWhiteSpace(request.Email) || !IsValidEmail(request.Email))
            throw new ArgumentException("A valid email address is required.");

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6)
            throw new ArgumentException("Password must be at least 6 characters.");
    }

    private static bool IsValidEmail(string email)
    {
        return System.Net.Mail.MailAddress.TryCreate(email, out _);
    }
}
