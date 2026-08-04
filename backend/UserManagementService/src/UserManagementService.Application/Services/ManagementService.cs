using AutoMapper;
using IntelliLect.Contracts.Messages;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;
using UserManagementService.Application.DTOs.User;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Application.Authentication;

public sealed class ManagementService : IManagementService
{
    private readonly IUserRepository _userRepository;
    private readonly IEventBus _eventBus;
    private readonly IHasher _hasher;
    private readonly IMapper _mapper;

    public ManagementService(IUserRepository userRepository, IHasher hasher, IMapper mapper, IEventBus eventBus)
    {
        _userRepository = userRepository;
        _hasher = hasher;
        _mapper = mapper;
        _eventBus = eventBus;
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

        if (!_hasher.Verify(request.OldPassword, user.PasswordHash))
            throw new UnauthorizedAccessException("Old password is incorrect.");

        user.UpdatePassword(_hasher.Hash(request.NewPassword));
        await _userRepository.UpdateAsync(user, ct);

        await _userRepository.SaveChangesAsync(ct);
    }

    
    public async Task<UserResponse> GetUserProfileAsync(Guid userId, CancellationToken ct)
    {
        var user = await _userRepository.GetByIdAsync(userId, ct);
        if (user == null) throw new ArgumentException("User not found.");

        return _mapper.Map<UserResponse>(user);
    }

    public async Task<PagedResult<UserResponse>> GetPendingUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct)
    {
        var (users, totalCount) = await _userRepository.GetPaginatedPendingUsersAsync(roleId, page, pageSize, ct);

        var mappedItems = _mapper.Map<List<UserResponse>>(users);

        return new PagedResult<UserResponse>(mappedItems, totalCount, page, pageSize);
    }

    public async Task<PagedResult<UserResponse>> GetAllUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct)
    {
        var (users, totalCount) = await _userRepository.GetPaginatedUsersAsync(roleId, page, pageSize, ct);

        var mappedItems = _mapper.Map<List<UserResponse>>(users);

        return new PagedResult<UserResponse>(mappedItems, totalCount, page, pageSize);
    }

    
    }
