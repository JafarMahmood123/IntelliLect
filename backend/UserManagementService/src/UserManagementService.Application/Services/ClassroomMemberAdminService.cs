using IntelliLect.Contracts.Messages;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;
using UserManagementService.Application.DTOs.Member;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Application.MemberAdministration;

public sealed class ClassroomMemberAdminService : IClassroomMemberAdminService
{
    private const string TeacherRole = "Teacher";
    private const string StudentRole = "Student";

    private readonly IClassroomInternalClient _classroomClient;
    private readonly IUserRepository _userRepository;
    private readonly INotificationBus _notificationBus;

    public ClassroomMemberAdminService(
        IClassroomInternalClient classroomClient,
        IUserRepository userRepository,
        INotificationBus notificationBus)
    {
        _classroomClient = classroomClient;
        _userRepository = userRepository;
        _notificationBus = notificationBus;
    }

    // Step 3: fetch the classroom's full membership set from ClassroomService (teacher + students),
    // enrich each with the user's name/email from the local store, then search + page in memory (a
    // single classroom's roster is bounded, and the filter fields live here, not in ClassroomService).
    public async Task<PagedResult<ClassroomMemberItem>> GetMembersAsync(
        Guid classroomId, SearchMembersRequest request, CancellationToken ct = default)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = Math.Clamp(request.PageSize < 1 ? 20 : request.PageSize, 1, 100);

        // 5أ: the client maps a 404 to NotFoundException.
        var data = await _classroomClient.GetClassroomMembersAsync(classroomId, ct);

        var userIds = new List<Guid> { data.TeacherId };
        userIds.AddRange(data.Students.Select(s => s.StudentId));
        var users = await _userRepository.GetByIdsAsync(userIds.Distinct().ToList(), ct);
        var userById = users.ToDictionary(u => u.Id);

        // The owning teacher first (shown for context, not removable — 5هـ), then students in join order.
        var members = new List<ClassroomMemberItem>
        {
            BuildItem(data.TeacherId, userById, TeacherRole, joinedAtUtc: null, isTeacher: true),
        };
        members.AddRange(data.Students.Select(s =>
            BuildItem(s.StudentId, userById, StudentRole, s.JoinedAtUtc, isTeacher: false)));

        var filtered = ApplySearch(members, request.Search);

        var pageItems = filtered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new PagedResult<ClassroomMemberItem>(pageItems, filtered.Count, page, pageSize);
    }

    public async Task<MemberChangeSummary> AddMemberAsync(
        Guid classroomId, AddMemberRequest request, CancellationToken ct = default)
    {
        // 5ب: the target must be an existing, active student.
        var student = await EnsureActiveStudentAsync(request.StudentId, ct);

        // 5أ (client -> NotFoundException). Changed=false is the 5ج no-op (already a member).
        var result = await _classroomClient.AddClassroomMemberAsync(classroomId, request.StudentId, ct);

        // Step 7: notify the student — only when they were actually added.
        if (result.Changed)
        {
            await NotifyAsync(student.Email, student.FirstName, result.ClassroomName, isAdded: true, ct);
        }

        return new MemberChangeSummary(result.Changed, classroomId, result.ClassroomName, request.StudentId, "Added");
    }

    public async Task<MemberChangeSummary> RemoveMemberAsync(
        Guid classroomId, Guid studentId, RemoveMemberRequest request, CancellationToken ct = default)
    {
        // 4أ: a removal reason is mandatory.
        if (string.IsNullOrWhiteSpace(request?.Reason))
        {
            throw new ArgumentException("A reason for removing the member is required.");
        }

        // 5أ/5د -> NotFoundException, 5هـ (teacher) -> InvalidOperationException (both from the client).
        var result = await _classroomClient.RemoveClassroomMemberAsync(classroomId, studentId, ct);

        // Step 7: notify the removed student (best-effort). Resolve their contact info locally.
        var student = (await _userRepository.GetByIdsAsync(new[] { studentId }, ct)).FirstOrDefault();
        if (student is not null)
        {
            await NotifyAsync(student.Email, student.FirstName, result.ClassroomName, isAdded: false, ct);
        }

        return new MemberChangeSummary(result.Changed, classroomId, result.ClassroomName, studentId, "Removed");
    }

    private static ClassroomMemberItem BuildItem(
        Guid userId, IReadOnlyDictionary<Guid, User> userById, string roleInClass, DateTime? joinedAtUtc, bool isTeacher)
    {
        userById.TryGetValue(userId, out var user);
        return new ClassroomMemberItem(
            userId,
            user is null ? null : $"{user.FirstName} {user.LastName}",
            user?.Email,
            roleInClass,
            joinedAtUtc,
            isTeacher);
    }

    private static List<ClassroomMemberItem> ApplySearch(List<ClassroomMemberItem> members, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return members;
        }

        var term = search.Trim();
        return members
            .Where(m =>
                (m.Name?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (m.Email?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
    }

    private async Task<User> EnsureActiveStudentAsync(Guid studentId, CancellationToken ct)
    {
        var student = (await _userRepository.GetByIdsAsync(new[] { studentId }, ct)).FirstOrDefault();

        if (student is null || student.Role.Name != RoleName.Student || student.Status != UserStatus.Active)
        {
            throw new ArgumentException("The member to add must be an existing, active user with the Student role.");
        }

        return student;
    }

    // Best-effort, direct (non-outbox) publish — the membership change is already committed in
    // ClassroomService and there is no local DB write to flush an outbox, so a broker outage must
    // not fail the operation.
    private async Task NotifyAsync(string email, string firstName, string classroomName, bool isAdded, CancellationToken ct)
    {
        try
        {
            await _notificationBus.PublishAsync(
                new ClassroomMembershipChangedMessage(email, firstName, classroomName, isAdded), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Notification delivery is a non-critical side-effect of a completed change — swallow.
        }
    }
}
