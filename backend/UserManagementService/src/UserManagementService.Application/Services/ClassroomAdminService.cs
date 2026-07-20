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

    public ClassroomAdminService(IClassroomInternalClient classroomClient, IUserRepository userRepository)
    {
        _classroomClient = classroomClient;
        _userRepository = userRepository;
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

    private async Task EnsureValidTeacherAsync(Guid teacherId, CancellationToken ct)
    {
        var teacher = (await _userRepository.GetByIdsAsync(new[] { teacherId }, ct)).FirstOrDefault();

        if (teacher is null || teacher.Role.Name != RoleName.Teacher || teacher.Status != UserStatus.Active)
        {
            throw new ArgumentException("The assigned teacher must be an existing, active user with the Teacher role.");
        }
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
