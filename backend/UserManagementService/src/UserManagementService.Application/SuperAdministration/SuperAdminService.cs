using AutoMapper;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;
using UserManagementService.Application.Common.Messages;
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

    public async Task DeactivateAdminAsync(Guid adminId, CancellationToken ct = default)
    {
        var admin = await _adminRepository.GetAdminByIdAsync(adminId, ct);
        if (admin == null)
        {
            throw new ArgumentException("Admin not found.");
        }

        admin.Deactivate();
        await _userRepository.UpdateAsync(admin, ct);
        await _eventBus.PublishAsync(new UserStatusChangedMessage(admin.Email, admin.FirstName, UserStatus.Deactivated), ct);
        await _userRepository.SaveChangesAsync(ct);
    }

    public async Task ReactivateAdminAsync(Guid adminId, CancellationToken ct = default)
    {
        var admin = await _adminRepository.GetAdminByIdAsync(adminId, ct);
        if (admin == null)
        {
            throw new ArgumentException("Admin not found.");
        }

        admin.Reactivate();
        await _userRepository.UpdateAsync(admin, ct);
        await _eventBus.PublishAsync(new UserStatusChangedMessage(admin.Email, admin.FirstName, UserStatus.Active), ct);
        await _userRepository.SaveChangesAsync(ct);
    }
}
