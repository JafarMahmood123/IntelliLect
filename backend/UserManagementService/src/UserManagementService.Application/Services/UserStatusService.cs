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
    /// <summary>
    /// Ceiling on one bulk request. A guard against a runaway or mistyped selection rather than a
    /// tuning knob, so it stays a constant (see docs/work-plan.md §14.2). Comfortably above a full
    /// page of pending registrations.
    /// </summary>
    public const int MaxBulkSize = 200;

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
        // Checked before the query as well as inside Decide, so the round trip is not spent.
        if (userId == requestingSuperAdminId)
        {
            throw new InvalidOperationException("You cannot change the status of your own account.");
        }

        // Load the account together with its sessions so they can be ended in the same
        // transaction when the account is deactivated or rejected.
        var user = await _userRepository.GetByIdWithRefreshTokensAsync(userId, ct);

        var outcome = Decide(userId, user, parsedAction, requestingSuperAdminId);

        switch (outcome)
        {
            // Alternate path 5أ: the target account does not exist.
            case ChangeOutcome.NotFound:
                throw new NotFoundException("User not found.");

            case ChangeOutcome.SelfTarget:
                throw new InvalidOperationException("You cannot change the status of your own account.");

            // Alternate path 5ج: the requested transition is not valid from the current state
            // (e.g. accepting a rejected account, or deactivating one that is not yet active).
            case ChangeOutcome.InvalidTransition:
                throw new InvalidOperationException(
                    $"Cannot {parsedAction} an account that is currently '{user!.Status}'.");

            // Alternate path 5د: the account is already in the requested state — a no-op. Nothing
            // is changed, no notification is sent, and the current profile is returned as-is.
            case ChangeOutcome.NoOp:
                return _mapper.Map<UserResponse>(user!);
        }

        // Step 7: notify the owner. Published through the transactional outbox, so it is
        // committed atomically with the status change and delivered asynchronously — a later
        // send failure (7أ) never rolls back or blocks the status change.
        await PublishStatusChangedAsync(user!, ct);

        await _userRepository.SaveChangesAsync(ct);

        return _mapper.Map<UserResponse>(user!);
    }

    /// <summary>
    /// One action applied to many accounts, for a super admin clearing a queue of pending
    /// registrations.
    ///
    /// NOT all-or-nothing: each account is decided and reported independently, so a single unknown
    /// id cannot sink 199 valid approvals. The per-account rules are the SAME code the
    /// single-account path runs (see <see cref="Decide"/>), so the two cannot drift — which
    /// matters, because "who may be approved" must not have two versions.
    ///
    /// One query for the batch, one notification per genuinely-changed account, one transaction.
    /// The outbox rows commit with the status changes, so a later delivery failure never undoes an
    /// approval that already happened.
    /// </summary>
    public async Task<BulkUserStatusResult> ChangeStatusBulkAsync(
        IReadOnlyCollection<Guid> userIds,
        string action,
        Guid requestingSuperAdminId,
        CancellationToken ct = default)
    {
        var parsedAction = ParseAction(action);

        if (userIds is null || userIds.Count == 0)
        {
            // An empty selection is a mistake, never "apply to everyone".
            throw new ArgumentException("Select at least one account.");
        }

        // Deduplicated so the same id listed twice cannot produce two notifications, and so the
        // cap counts accounts rather than list entries.
        var distinctIds = userIds.Distinct().ToList();

        if (distinctIds.Count > MaxBulkSize)
        {
            throw new ArgumentException(
                $"A bulk status change accepts at most {MaxBulkSize} accounts; {distinctIds.Count} were requested.");
        }

        var users = await _userRepository.GetByIdsWithRefreshTokensAsync(distinctIds, ct);
        var usersById = users.ToDictionary(u => u.Id);

        var results = new List<BulkUserStatusItem>(distinctIds.Count);
        var changedCount = 0;

        foreach (var userId in distinctIds)
        {
            usersById.TryGetValue(userId, out var user);

            // Authorization and validity are evaluated PER ACCOUNT, never once for the batch.
            var outcome = Decide(userId, user, parsedAction, requestingSuperAdminId);

            if (outcome is ChangeOutcome.Changed)
            {
                await PublishStatusChangedAsync(user!, ct);
                changedCount++;
            }

            results.Add(ToItem(userId, user, parsedAction, outcome));
        }

        // Skipped when nothing changed, so a batch of pure no-ops writes nothing.
        if (changedCount > 0)
        {
            await _userRepository.SaveChangesAsync(ct);
        }

        return BulkUserStatusResult.From(results);
    }

    // --- the shared decision, used by both paths ---------------------------------

    private enum ChangeOutcome
    {
        /// <summary>Applied. The caller must publish and save.</summary>
        Changed,

        /// <summary>Already in the requested state. Success, but nothing to write or notify.</summary>
        NoOp,

        NotFound,
        SelfTarget,
        InvalidTransition,
    }

    /// <summary>
    /// Decides what should happen to one account, and APPLIES the change when it is valid.
    ///
    /// Deliberately the single home of these rules. Both entry points call it, so the bulk path
    /// cannot quietly accept something the single path refuses — the failure mode that makes bulk
    /// operations dangerous.
    /// </summary>
    private static ChangeOutcome Decide(
        Guid userId, User? user, UserStatusAction action, Guid requestingSuperAdminId)
    {
        if (userId == requestingSuperAdminId)
        {
            return ChangeOutcome.SelfTarget;
        }

        if (user is null)
        {
            return ChangeOutcome.NotFound;
        }

        var target = TargetStatus(action);

        if (user.Status == target)
        {
            return ChangeOutcome.NoOp;
        }

        if (!IsValidSource(action, user.Status))
        {
            return ChangeOutcome.InvalidTransition;
        }

        ApplyTransition(user, action);

        // Deactivation/rejection revoke all active sessions so the user cannot renew their
        // session; the short-lived access token then lapses.
        if (target is UserStatus.Deactivated or UserStatus.Rejected)
        {
            RevokeActiveSessions(user);
        }

        return ChangeOutcome.Changed;
    }

    private static BulkUserStatusItem ToItem(
        Guid userId, User? user, UserStatusAction action, ChangeOutcome outcome) => outcome switch
    {
        ChangeOutcome.Changed =>
            new BulkUserStatusItem(userId, true, user!.Status.ToString(), null),

        // A no-op is a SUCCESS: re-approving an already-approved account is exactly what a retried
        // request looks like, and reporting it as a failure would make retries unusable.
        ChangeOutcome.NoOp =>
            new BulkUserStatusItem(userId, true, user!.Status.ToString(), null),

        ChangeOutcome.NotFound =>
            new BulkUserStatusItem(userId, false, null, "Account not found."),

        ChangeOutcome.SelfTarget =>
            new BulkUserStatusItem(userId, false, user?.Status.ToString(),
                "You cannot change the status of your own account."),

        ChangeOutcome.InvalidTransition =>
            new BulkUserStatusItem(userId, false, user!.Status.ToString(),
                $"Cannot {action} an account that is currently '{user.Status}'."),

        _ => new BulkUserStatusItem(userId, false, null, "Unknown outcome."),
    };

    private Task PublishStatusChangedAsync(User user, CancellationToken ct) =>
        _eventBus.PublishAsync(
            new UserStatusChangedMessage(user.Email, user.FirstName, user.Status.ToString()), ct);

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
