using AutoMapper;
using IntelliLect.Contracts.Messages;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;
using UserManagementService.Application.Common.Users;
using UserManagementService.Application.DTOs.User;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Application.UserAccounts;

public sealed class UserStatusService : IUserStatusService
{
    private readonly IUserRepository _userRepository;
    private readonly IEventBus _eventBus;
    private readonly IMapper _mapper;

    public UserStatusService(IUserRepository userRepository, IEventBus eventBus, IMapper mapper)
    {
        _userRepository = userRepository;
        _eventBus = eventBus;
        _mapper = mapper;
    }

    public async Task<UserResponse> ChangeStatusAsync(
        Guid userId,
        string action,
        Guid requestingSuperAdminId,
        CancellationToken ct = default)
    {
        var parsedAction = ParseAction(action);

        // Alternate path 5ب: a super admin must not change the status of their own account.
        if (userId == requestingSuperAdminId)
        {
            throw new InvalidOperationException("You cannot change the status of your own account.");
        }

        // Load the account together with its sessions so they can be ended in the same
        // transaction when the account is deactivated or rejected.
        var user = await _userRepository.GetByIdWithRefreshTokensAsync(userId, ct);

        // Alternate path 5أ: the target account does not exist.
        if (user == null)
        {
            throw new NotFoundException("User not found.");
        }

        var target = TargetStatus(parsedAction);

        // Alternate path 5د: the account is already in the requested state — a no-op. Nothing
        // is changed, no notification is sent, and the current profile is returned as-is.
        if (user.Status == target)
        {
            return _mapper.Map<UserResponse>(user);
        }

        // Alternate path 5ج: the requested transition is not valid from the current state
        // (e.g. accepting a rejected account, or deactivating one that is not yet active).
        if (!IsValidSource(parsedAction, user.Status))
        {
            throw new InvalidOperationException(
                $"Cannot {parsedAction} an account that is currently '{user.Status}'.");
        }

        // Step 6: apply the transition.
        ApplyTransition(user, parsedAction);

        // Step 6 (cont.): ending access. Deactivation/rejection revoke all active sessions so
        // the user cannot renew their session; the short-lived access token then lapses.
        if (target is UserStatus.Deactivated or UserStatus.Rejected)
        {
            RevokeActiveSessions(user);
        }

        // Step 7: notify the owner. Published through the transactional outbox, so it is
        // committed atomically with the status change and delivered asynchronously — a later
        // send failure (7أ) never rolls back or blocks the status change.
        await _eventBus.PublishAsync(
            new UserStatusChangedMessage(user.Email, user.FirstName, target.ToString()), ct);

        await _userRepository.SaveChangesAsync(ct);

        return _mapper.Map<UserResponse>(user);
    }

    private static UserStatusAction ParseAction(string action)
    {
        if (!Enum.TryParse<UserStatusAction>((action ?? string.Empty).Trim(), ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            throw new ArgumentException("Invalid status action.");
        }

        return parsed;
    }

    private static UserStatus TargetStatus(UserStatusAction action) => action switch
    {
        UserStatusAction.Accept => UserStatus.Active,
        UserStatusAction.Reject => UserStatus.Rejected,
        UserStatusAction.Deactivate => UserStatus.Deactivated,
        UserStatusAction.Reactivate => UserStatus.Active,
        _ => throw new ArgumentException("Invalid status action.")
    };

    // The only state an account may be in for each action to be valid.
    private static bool IsValidSource(UserStatusAction action, UserStatus current) => action switch
    {
        UserStatusAction.Accept => current == UserStatus.Pending,
        UserStatusAction.Reject => current == UserStatus.Pending,
        UserStatusAction.Deactivate => current == UserStatus.Active,
        UserStatusAction.Reactivate => current == UserStatus.Deactivated,
        _ => false
    };

    private static void ApplyTransition(User user, UserStatusAction action)
    {
        switch (action)
        {
            case UserStatusAction.Accept:
                user.Approve();
                break;
            case UserStatusAction.Reject:
                user.Reject();
                break;
            case UserStatusAction.Deactivate:
                user.Deactivate();
                break;
            case UserStatusAction.Reactivate:
                user.Reactivate();
                break;
        }
    }

    private static void RevokeActiveSessions(User user)
    {
        foreach (var token in user.RefreshTokens.Where(t => t.IsActive))
        {
            token.Revoke();
        }
    }
}
