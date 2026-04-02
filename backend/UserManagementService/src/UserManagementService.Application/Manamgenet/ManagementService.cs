using AutoMapper;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.DTOs.User;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Application.Authentication;

public sealed class ManagementService : IManagementService
{
    private readonly IUserRepository _userRepository;
    private readonly IHasher _hasher;
    private readonly IMapper _mapper;

    public ManagementService(IUserRepository userRepository, IHasher hasher, IMapper mapper)
    {
        _userRepository = userRepository;
        _hasher = hasher;
        _mapper = mapper;
    }

    public async Task UpdateUserAsync(Guid userId, UpdateUserRequest request, CancellationToken ct = default)
    {
        var user = await _userRepository.GetByIdAsync(userId, ct);
        if (user == null) throw new ArgumentException("User not found.");

        user.UpdateInfo(request.FirstName, request.LastName, request.UserName, request.Bio);

        await _userRepository.UpdateAsync(user, ct);
        await _userRepository.SaveChangesAsync(ct);
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct)
    {
        var user = await _userRepository.GetByIdAsync(userId, ct);
        if (user == null) throw new ArgumentException("User not found.");

        if (!_hasher.VerifyPassword(request.OldPassword, user.PasswordHash))
            throw new UnauthorizedAccessException("Old password is incorrect.");

        user.UpdatePassword(_hasher.HashPassword(request.NewPassword));
        await _userRepository.UpdateAsync(user, ct);

        await _userRepository.SaveChangesAsync(ct);
    }

    public async Task DeactivateUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await _userRepository.GetByIdAsync(userId, ct);
        if (user != null)
        {
            user.Deactivate();
            await _userRepository.UpdateAsync(user, ct);

            await _userRepository.SaveChangesAsync(ct);
        }
    }

    public async Task<UserResponse> GetUserProfileAsync(Guid userId, CancellationToken ct)
    {
        var user = await _userRepository.GetByIdAsync(userId, ct);
        if (user == null) throw new ArgumentException("User not found.");

        return _mapper.Map<UserResponse>(user);
    }

    public async Task<List<UserResponse>> GetPendingUsersAsync(CancellationToken ct)
    {
        var users = await _userRepository.GetPendingUsrs(ct);

        return _mapper.Map<List<UserResponse>>(users);
    }

    public async Task ChangeUserStatus(Guid userId, UserStatus newStatus, CancellationToken ct)
    {
        var user = await _userRepository.GetByIdAsync(userId, ct);
        if (user == null) throw new ArgumentException("User not found.");

        if (newStatus == UserStatus.Active) user.Approve();
        else if (newStatus == UserStatus.Rejected) user.Reject();
        else throw new ArgumentException("Invalid status update.");

        await _userRepository.UpdateAsync(user, ct);
        await _userRepository.SaveChangesAsync(ct);
    }
}