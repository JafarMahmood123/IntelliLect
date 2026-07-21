using IntelliLect.Contracts.Messages;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;
using UserManagementService.Application.DTOs.Classroom;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Application.ClassroomAdministration;

public sealed class ClassroomAdminService : IClassroomAdminService
{
    private const int MaxNameLength = 100;

    private readonly IClassroomInternalClient _classroomClient;
    private readonly IUserRepository _userRepository;
    private readonly INotificationBus _notificationBus;

    public ClassroomAdminService(
        IClassroomInternalClient classroomClient,
        IUserRepository userRepository,
        INotificationBus notificationBus)
    {
        _classroomClient = classroomClient;
        _userRepository = userRepository;
        _notificationBus = notificationBus;
    }

    // Steps 3-5 (list): fetch the classroom page from ClassroomService, then attach each
    // classroom's teacher name/email from the local user store.
    public async Task<PagedResult<ClassroomAdminItem>> GetClassroomsAsync(SearchClassroomsRequest request, CancellationToken ct = default)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = Math.Clamp(request.PageSize < 1 ? 20 : request.PageSize, 1, 100);

        var result = await _classroomClient.GetClassroomsAsync(page, pageSize, request.Search, request.TeacherId, ct);

        var teacherIds = result.Items.Select(c => c.TeacherId).Distinct().ToList();
        var teachers = await _userRepository.GetByIdsAsync(teacherIds, ct);
        var teacherById = teachers.ToDictionary(u => u.Id);

        var items = result.Items
            .Select(c =>
            {
                teacherById.TryGetValue(c.TeacherId, out var teacher);
                return new ClassroomAdminItem(
                    c.Id,
                    c.Name,
                    c.Description,
                    c.TeacherId,
                    teacher is null ? null : $"{teacher.FirstName} {teacher.LastName}",
                    teacher?.Email,
                    c.CreatedAtUtc,
                    c.FileCount,
                    c.StudentCount,
                    c.SessionCount,
                    c.Version,
                    c.Status);
            })
            .ToList();

        return new PagedResult<ClassroomAdminItem>(items, result.TotalCount, page, pageSize);
    }

    public async Task<Guid> CreateClassroomAsync(CreateClassroomAdminRequest request, CancellationToken ct = default)
    {
        // Alternate path 5أ: required, well-formed data.
        var name = ValidateName(request.Name);
        var description = ValidateDescription(request.Description);

        // Alternate path 5ب: the assigned teacher must be an existing, active user with the Teacher role.
        await EnsureValidTeacherAsync(request.TeacherId, ct);

        return await _classroomClient.CreateClassroomAsync(request.TeacherId, name, description, ct);
    }

    public async Task UpdateClassroomAsync(Guid classroomId, UpdateClassroomAdminRequest request, CancellationToken ct = default)
    {
        var name = ValidateName(request.Name);
        var description = ValidateDescription(request.Description);

        // The client maps ClassroomService's responses to NotFoundException (5ج, 404) and
        // InvalidOperationException (6أ concurrency, 409).
        await _classroomClient.UpdateClassroomAsync(classroomId, name, description, request.Version, ct);
    }

    // Step 3: read-only impact preview. Returns null (-> 404) if the classroom does not exist (5أ).
    public async Task<ClassroomDeletionImpactResult?> GetDeletionImpactAsync(Guid classroomId, CancellationToken ct = default)
    {
        var impact = await _classroomClient.GetClassroomDeletionImpactAsync(classroomId, ct);
        if (impact is null)
        {
            return null;
        }

        return new ClassroomDeletionImpactResult(
            impact.ClassroomId,
            impact.Name,
            impact.Status,
            impact.SessionCount,
            impact.MemberCount,
            impact.FileCount,
            impact.RecordingCount,
            impact.SummaryCount,
            impact.StorageBytes,
            impact.HasLiveSession);
    }

    public async Task<ClassroomDeletionSummary> DeleteClassroomAsync(
        Guid classroomId, DeleteClassroomAdminRequest request, CancellationToken ct = default)
    {
        // 4أ: refuse without a reason (and thus without the deliberate confirmation it represents),
        // guarding against an accidental deletion — validated here before the cross-service call, and
        // again in ClassroomService.
        if (string.IsNullOrWhiteSpace(request?.Reason))
        {
            throw new ArgumentException("A deletion reason is required.");
        }

        // The client maps ClassroomService's 404 -> NotFoundException (5أ) and 409 ->
        // InvalidOperationException (5ب, live session).
        var result = await _classroomClient.DeleteClassroomAsync(classroomId, request.Reason.Trim(), ct);

        return new ClassroomDeletionSummary(
            result.ClassroomId,
            result.RecordingsDeleted,
            result.SummariesDeleted,
            result.FilesDeleted,
            result.SessionsDeleted,
            result.MembershipsDeleted);
    }

    public async Task<ClassroomTeacherChangeSummary> ChangeTeacherAsync(
        Guid classroomId, ChangeClassroomTeacherRequest request, CancellationToken ct = default)
    {
        // 1أ: a reason is mandatory — it documents why ownership moved (validated here before the
        // cross-service call, and again in ClassroomService).
        if (string.IsNullOrWhiteSpace(request?.Reason))
        {
            throw new ArgumentException("A reason for the teacher change is required.");
        }

        // 4أ: the new teacher must be an existing, active user with the Teacher role.
        var newTeacher = await EnsureValidTeacherAsync(request.NewTeacherId, ct);

        // ClassroomService verifies the classroom exists (3أ -> NotFoundException), that no live
        // session is running (3ب -> InvalidOperationException), performs the 4ب no-op when the new
        // teacher already owns it, and transfers ownership under optimistic concurrency (step 5).
        var change = await _classroomClient.ChangeClassroomTeacherAsync(
            classroomId, request.NewTeacherId, request.Version, ct);

        // Step 6: notify both teachers — only when ownership actually moved (skip the 4ب no-op).
        if (change.Changed)
        {
            await NotifyTeachersAsync(change, newTeacher, ct);
        }

        return new ClassroomTeacherChangeSummary(
            classroomId, change.Changed, change.PreviousTeacherId, change.NewTeacherId, change.ClassroomName);
    }

    // Step 6: best-effort notification. The transfer is already committed in ClassroomService, so a
    // broker outage must not fail the operation; delivery is direct (not through the outbox) because
    // this path performs no local database write to flush one.
    private async Task NotifyTeachersAsync(ClassroomTeacherChange change, User newTeacher, CancellationToken ct)
    {
        try
        {
            var previous = (await _userRepository.GetByIdsAsync(new[] { change.PreviousTeacherId }, ct)).FirstOrDefault();
            if (previous is not null)
            {
                await _notificationBus.PublishAsync(
                    new ClassroomTeacherChangedMessage(previous.Email, previous.FirstName, change.ClassroomName, IsNewTeacher: false),
                    ct);
            }

            await _notificationBus.PublishAsync(
                new ClassroomTeacherChangedMessage(newTeacher.Email, newTeacher.FirstName, change.ClassroomName, IsNewTeacher: true),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Notification delivery is a non-critical side-effect of a completed transfer — swallow.
        }
    }

    private async Task<User> EnsureValidTeacherAsync(Guid teacherId, CancellationToken ct)
    {
        var teacher = (await _userRepository.GetByIdsAsync(new[] { teacherId }, ct)).FirstOrDefault();

        if (teacher is null || teacher.Role.Name != RoleName.Teacher || teacher.Status != UserStatus.Active)
        {
            throw new ArgumentException("The assigned teacher must be an existing, active user with the Teacher role.");
        }

        return teacher;
    }

    private static string ValidateName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Classroom name is required.");
        }
        if (trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException($"Classroom name must be at most {MaxNameLength} characters.");
        }
        return trimmed;
    }

    private static string ValidateDescription(string? description)
    {
        var trimmed = (description ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Classroom description is required.");
        }
        return trimmed;
    }
}
