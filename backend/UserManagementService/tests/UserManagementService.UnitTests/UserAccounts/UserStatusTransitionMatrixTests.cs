using UserManagementService.Application.Common.Users;
using UserManagementService.Application.UserAccounts;
using UserManagementService.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace UserManagementService.UnitTests.UserAccounts;

/// <summary>
/// The whole status transition matrix, every combination (test-plan C-01, C-02, C-12).
///
/// C-02 was marked `partial`, and this is why: the transitions were tested from three hand-written
/// `InlineData` lists — the legal ones, the no-ops, the refusals — covering thirteen of the sixteen
/// combinations the two enums allow. The three nobody listed were `Rejected → Deactivate`,
/// `Rejected → Reactivate` and `Deactivated → Reject`. **The first of those is the example the
/// test-plan row itself names.** That is what a hand-written list does: it looks complete, because
/// the cases in it are the cases somebody thought of.
///
/// So the list is replaced by the enums. Every `UserStatus` crossed with every `UserStatusAction`,
/// each with a declared outcome, and a case that fails when the enums grow past the table — because
/// adding a fifth status would otherwise be silently refused everywhere by `IsValidSource`'s
/// `_ => false`, with no test anywhere noticing that nobody had decided its rules.
///
/// The two superseded `InlineData` theories in <c>UserStatusServiceTests</c> are removed rather
/// than left beside this. One rule, one place: §11.7 spent two surviving mutations learning that a
/// rule written twice eventually disagrees with itself, and this one is written in two paths
/// already (single and bulk), which is the disagreement actually worth guarding.
/// </summary>
public sealed class UserStatusTransitionMatrixTests
{
    private static readonly Guid SuperAdminId = Guid.NewGuid();

    private enum Outcome
    {
        /// <summary>The change applies: status moves, the owner is notified, one save.</summary>
        Applied,

        /// <summary>Already there. A success, but nothing written and nobody notified.</summary>
        NoOp,

        /// <summary>Not reachable from this state. Refused, nothing touched.</summary>
        Refused,
    }

    /// <summary>
    /// Every combination the two enums allow, and what each one must do.
    ///
    /// Written out in full deliberately. It could be derived from the same rules the service uses,
    /// but a test that recomputes the implementation cannot disagree with it — it would pass
    /// against any consistent set of rules, including the wrong one.
    /// </summary>
    private static readonly Dictionary<(UserStatus From, UserStatusAction Action), Outcome> Matrix = new()
    {
        [(UserStatus.Pending, UserStatusAction.Accept)] = Outcome.Applied,
        [(UserStatus.Pending, UserStatusAction.Reject)] = Outcome.Applied,
        [(UserStatus.Pending, UserStatusAction.Deactivate)] = Outcome.Refused,
        [(UserStatus.Pending, UserStatusAction.Reactivate)] = Outcome.Refused,

        [(UserStatus.Active, UserStatusAction.Accept)] = Outcome.NoOp,
        [(UserStatus.Active, UserStatusAction.Reject)] = Outcome.Refused,
        [(UserStatus.Active, UserStatusAction.Deactivate)] = Outcome.Applied,
        [(UserStatus.Active, UserStatusAction.Reactivate)] = Outcome.NoOp,

        [(UserStatus.Rejected, UserStatusAction.Accept)] = Outcome.Refused,
        [(UserStatus.Rejected, UserStatusAction.Reject)] = Outcome.NoOp,
        // The row the plan names as its example, and the one that was never tested. Rejection is
        // meant to be terminal: an administrator who wants a rejected applicant gone cannot get
        // there by deactivating them, and must not be able to.
        [(UserStatus.Rejected, UserStatusAction.Deactivate)] = Outcome.Refused,
        [(UserStatus.Rejected, UserStatusAction.Reactivate)] = Outcome.Refused,

        [(UserStatus.Deactivated, UserStatusAction.Accept)] = Outcome.Refused,
        [(UserStatus.Deactivated, UserStatusAction.Reject)] = Outcome.Refused,
        [(UserStatus.Deactivated, UserStatusAction.Deactivate)] = Outcome.NoOp,
        [(UserStatus.Deactivated, UserStatusAction.Reactivate)] = Outcome.Applied,
    };

    public static TheoryData<UserStatus, UserStatusAction> EveryCombination()
    {
        var data = new TheoryData<UserStatus, UserStatusAction>();
        foreach (var status in Enum.GetValues<UserStatus>())
        {
            foreach (var action in Enum.GetValues<UserStatusAction>())
            {
                data.Add(status, action);
            }
        }
        return data;
    }

    [Fact]
    public void The_table_covers_every_combination_the_enums_allow()
    {
        // The case that makes this a rule rather than a longer list. A new UserStatus or
        // UserStatusAction would be silently refused everywhere — `IsValidSource` ends in
        // `_ => false` and `TargetStatus` in a throw — and every test above would still pass,
        // because none of them would know to ask. This fails until somebody decides the new
        // member's rules and writes them down.
        var expected = Enum.GetValues<UserStatus>().Length * Enum.GetValues<UserStatusAction>().Length;

        Assert.Equal(expected, Matrix.Count);
    }

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public async Task The_single_account_path_follows_the_matrix(UserStatus from, UserStatusAction action)
    {
        var expected = Matrix[(from, action)];
        var user = UserWith(from, ActiveToken());
        var repo = new FakeStatusUserRepository(user);
        var bus = new RecordingStatusEventBus();
        var sut = new UserStatusService(repo, bus, TestMapper.Create(), NullLogger<UserStatusService>.Instance);

        if (expected is Outcome.Refused)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => sut.ChangeStatusAsync(user.Id, action.ToString(), SuperAdminId));

            Assert.Equal(from, user.Status);
            Assert.False(user.RefreshTokens.Single().IsRevoked);
            Assert.Equal(0, repo.SaveChangesCallCount);
            Assert.Empty(bus.Published);
            return;
        }

        var result = await sut.ChangeStatusAsync(user.Id, action.ToString(), SuperAdminId);

        Assert.Equal(TargetOf(action), Enum.Parse<UserStatus>(result.Status));

        if (expected is Outcome.NoOp)
        {
            // A success that writes nothing and re-notifies nobody. This is what makes the
            // endpoint safe to retry, so "no save, no message" is the assertion, not the status.
            Assert.Equal(from, user.Status);
            Assert.Equal(0, repo.SaveChangesCallCount);
            Assert.Empty(bus.Published);
            return;
        }

        Assert.Equal(TargetOf(action), user.Status);
        Assert.Equal(1, repo.SaveChangesCallCount);
        var message = Assert.Single(bus.Published);
        Assert.Equal(TargetOf(action).ToString(), Assert.IsType<IntelliLect.Contracts.Messages.UserStatusChangedMessage>(message).Status);
    }

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public async Task The_bulk_path_follows_the_same_matrix(UserStatus from, UserStatusAction action)
    {
        // The failure this guards is bulk quietly accepting something the single path refuses.
        // Both call the same `Decide`, which is exactly why it needs a test: the two are one
        // edit away from diverging, and a bulk endpoint that is more permissive than the single
        // one is the shape that lets an administrator do in fifty clicks what they cannot do in
        // one. The reporting differs on purpose — a refusal is an exception on one path and a
        // named per-item failure on the other — and that difference is asserted rather than
        // worked around.
        var expected = Matrix[(from, action)];
        var user = UserWith(from, ActiveToken());
        var repo = new FakeStatusUserRepository(user);
        var bus = new RecordingStatusEventBus();
        var sut = new UserStatusService(repo, bus, TestMapper.Create(), NullLogger<UserStatusService>.Instance);

        var result = await sut.ChangeStatusBulkAsync([user.Id], action.ToString(), SuperAdminId);

        var item = Assert.Single(result.Results);

        if (expected is Outcome.Refused)
        {
            Assert.False(item.Succeeded);
            Assert.NotNull(item.Error);
            Assert.Equal(from, user.Status);
            Assert.Equal(0, repo.SaveChangesCallCount);
            Assert.Empty(bus.Published);
            return;
        }

        Assert.True(item.Succeeded);
        Assert.Null(item.Error);

        if (expected is Outcome.NoOp)
        {
            Assert.Equal(from, user.Status);
            Assert.Equal(0, repo.SaveChangesCallCount);
            Assert.Empty(bus.Published);
            return;
        }

        Assert.Equal(TargetOf(action), user.Status);
        Assert.Equal(1, repo.SaveChangesCallCount);
        Assert.Single(bus.Published);
    }

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public async Task Sessions_end_exactly_when_the_account_stops_being_usable(
        UserStatus from, UserStatusAction action)
    {
        // Tied to the matrix rather than tested per transition, because the rule is about the
        // DESTINATION, not the action: an account that lands on Rejected or Deactivated can no
        // longer sign in, so anything still able to renew a session has to be revoked. Approving
        // or reactivating must not revoke — that would sign a user out at the moment they were
        // granted access, for no reason they could see.
        var expected = Matrix[(from, action)];
        var token = ActiveToken();
        var user = UserWith(from, token);
        var repo = new FakeStatusUserRepository(user);
        var sut = new UserStatusService(repo, new RecordingStatusEventBus(), TestMapper.Create(), NullLogger<UserStatusService>.Instance);

        try
        {
            await sut.ChangeStatusAsync(user.Id, action.ToString(), SuperAdminId);
        }
        catch (InvalidOperationException) when (expected is Outcome.Refused)
        {
            // Covered above; here only the token matters.
        }

        var shouldBeRevoked = expected is Outcome.Applied
            && TargetOf(action) is UserStatus.Rejected or UserStatus.Deactivated;

        Assert.Equal(shouldBeRevoked, token.IsRevoked);
    }

    // --- helpers ------------------------------------------------------------------------

    /// <summary>
    /// Where each action is trying to get to. Spelled out here rather than reused from the
    /// service, so a change to the service's own mapping fails these instead of moving with them.
    /// </summary>
    private static UserStatus TargetOf(UserStatusAction action) => action switch
    {
        UserStatusAction.Accept => UserStatus.Active,
        UserStatusAction.Reject => UserStatus.Rejected,
        UserStatusAction.Deactivate => UserStatus.Deactivated,
        UserStatusAction.Reactivate => UserStatus.Active,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static RefreshToken ActiveToken() => new()
    {
        Id = Guid.NewGuid(),
        Token = Guid.NewGuid().ToString("N"),
        IsRevoked = false,
        ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
    };

    private static User UserWith(UserStatus status, params RefreshToken[] tokens)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = "user",
            Email = "user@intellilect.io",
            FirstName = "First",
            LastName = "Last",
            PasswordHash = "H:pass",
            RoleId = Guid.NewGuid(),
            Role = Role.Create(RoleName.Student),
            RefreshTokens = tokens.ToList(),
        };

        // Driven into the starting state through the entity's own transitions, not by reflection.
        switch (status)
        {
            case UserStatus.Active: user.Approve(); break;
            case UserStatus.Rejected: user.Reject(); break;
            case UserStatus.Deactivated: user.Approve(); user.Deactivate(); break;
            case UserStatus.Pending: break;
        }

        return user;
    }
}
