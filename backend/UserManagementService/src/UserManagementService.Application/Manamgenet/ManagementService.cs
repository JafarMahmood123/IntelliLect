using UserManagementService.Application.Abstractions;
using UserManagementService.Application.DTOs;

namespace UserManagementService.Application.Authentication;

public sealed class ManagementService : IManagementService
{
    private readonly IUserRepository _userRepository;
    private readonly IHasher _hasher;

    public ManagementService(IUserRepository userRepository, IHasher hasher)
    {
        _userRepository = userRepository;
        _hasher = hasher;
    }

    public async Task UpdateUserAsync(Guid userId, UpdateUserRequest request, CancellationToken ct)
    {
        var user = await _userRepository.GetByIdAsync(userId, ct);
        if (user == null) throw new ArgumentException("User not found.");

        user.UpdateInfo(request.FirstName, request.LastName, request.UserName);
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
}