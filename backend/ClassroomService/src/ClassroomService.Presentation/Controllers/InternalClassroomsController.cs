using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Classroom;
using ClassroomService.Application.DTOs.Membership;
using ClassroomService.Application.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ClassroomService.Presentation.Filters;

namespace ClassroomService.Presentation.Controllers;

/// <summary>
/// Service-to-service classroom administration for UserManagementService's super-admin
/// features. Not proxied by nginx (only <c>/api/classrooms</c> is public), so it is reachable
/// only on the internal docker network; when an internal secret is configured it must be
/// supplied via the <c>X-Internal-Secret</c> header. Teacher validation and the 2FA-gated
/// authorization happen in the caller (UserManagementService).
/// </summary>
[ApiController]
[Route("api/internal/classrooms")]
[InternalSecret]
public sealed class InternalClassroomsController : ControllerBase
{
    private readonly IClassroomManagementService _classrooms;
    private readonly IClassroomDeletionService _deletion;
    private readonly IClassroomMemberAdminService _members;
    private readonly IClassroomRepository _classroomRepository;

    public InternalClassroomsController(
        IClassroomManagementService classrooms,
        IClassroomDeletionService deletion,
        IClassroomMemberAdminService members,
        IClassroomRepository classroomRepository)
    {
        _classrooms = classrooms;
        _deletion = deletion;
        _members = members;
        _classroomRepository = classroomRepository;
    }

    [HttpGet]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page,
        [FromQuery] int pageSize,
        [FromQuery] string? search,
        [FromQuery] Guid? teacherId,
        CancellationToken ct)
    {
        var normalizedPage = page < 1 ? 1 : page;
        var normalizedPageSize = Math.Clamp(pageSize < 1 ? 20 : pageSize, 1, 100);

        var result = await _classrooms.GetAdminPagedAsync(normalizedPage, normalizedPageSize, search, teacherId, ct);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(AdminClassroomResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var classroom = await _classrooms.GetAdminByIdAsync(id, ct);
        return classroom is null ? NotFound() : Ok(classroom);
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] InternalCreateClassroomRequest request, CancellationToken ct)
    {
        var id = await _classrooms.CreateAsync(
            request.TeacherId,
            new CreateClassroomRequest(request.Name, request.Description),
            ct);

        return StatusCode(StatusCodes.Status201Created, new { id });
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, [FromBody] InternalUpdateClassroomRequest request, CancellationToken ct)
    {
        try
        {
            await _classrooms.AdminUpdateAsync(
                id,
                new UpdateClassroomRequest(request.Name, request.Description),
                request.Version,
                ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            // Alternate path 5ج.
            return NotFound();
        }
        catch (ConflictException)
        {
            // Alternate path 6أ: the classroom changed since it was read.
            return Conflict();
        }
    }

    /// <summary>
    /// Ownership transfer: reassign the classroom to a new teacher. The caller has already
    /// validated the new teacher (active Teacher, 4أ) and the reason (1أ).
    /// </summary>
    [HttpPut("{id:guid}/teacher")]
    [ProducesResponseType(typeof(ChangeTeacherResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ChangeTeacher(Guid id, [FromBody] InternalChangeTeacherRequest request, CancellationToken ct)
    {
        try
        {
            var result = await _classrooms.ChangeTeacherAsync(id, request.NewTeacherId, request.Version, ct);
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            // 3أ: the classroom does not exist.
            return NotFound();
        }
        catch (ConflictException)
        {
            // 3ب: a live session is in progress, or the version is stale.
            return Conflict();
        }
    }

    /// <summary>
    /// Step 3: read-only deletion impact preview. 404 if the classroom does not exist (5أ).
    /// </summary>
    [HttpGet("{id:guid}/deletion-impact")]
    [ProducesResponseType(typeof(ClassroomDeletionImpact), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDeletionImpact(Guid id, CancellationToken ct)
    {
        var impact = await _deletion.GetImpactAsync(id, ct);
        return impact is null ? NotFound() : Ok(impact);
    }

    /// <summary>
    /// Steps 5-6: delete the classroom and everything it owns. Idempotent/resumable — re-issuing a
    /// delete that previously failed part-way continues from where it stopped (6أ).
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(typeof(ClassroomDeletionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, [FromBody] InternalDeleteClassroomRequest request, CancellationToken ct)
    {
        try
        {
            var result = await _deletion.DeleteAsync(id, request?.Reason ?? string.Empty, ct);
            return Ok(result);
        }
        catch (ArgumentException)
        {
            // 4أ: missing reason/confirmation.
            return BadRequest();
        }
        catch (KeyNotFoundException)
        {
            // 5أ: classroom does not exist.
            return NotFound();
        }
        catch (ConflictException)
        {
            // 5ب: a live session is in progress.
            return Conflict();
        }
    }

    // ----- Member management (list / add / remove students) --------------------

    /// <summary>Step 3: the full membership set (teacher + students) for the super-admin member view.
    /// 404 if the classroom does not exist (5أ).</summary>
    [HttpGet("{id:guid}/members")]
    [ProducesResponseType(typeof(ClassroomMembersResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMembers(Guid id, CancellationToken ct)
    {
        try
        {
            return Ok(await _members.GetMembersAsync(id, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(); // 5أ
        }
    }

    /// <summary>Steps 5-6: add a student. No-op (Changed=false) when already a member (5ج).</summary>
    [HttpPost("{id:guid}/members")]
    [ProducesResponseType(typeof(MemberMutationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddMember(Guid id, [FromBody] InternalAddMemberRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(await _members.AddMemberAsync(id, request.StudentId, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(); // 5أ
        }
    }

    /// <summary>Steps 5-6: remove a member. The removal reason is validated by the caller (4أ).</summary>
    [HttpDelete("{id:guid}/members/{studentId:guid}")]
    [ProducesResponseType(typeof(MemberMutationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveMember(Guid id, Guid studentId, CancellationToken ct)
    {
        try
        {
            return Ok(await _members.RemoveMemberAsync(id, studentId, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(); // 5أ (classroom) / 5د (membership)
        }
        catch (ConflictException)
        {
            return Conflict(); // 5هـ: target is the classroom teacher
        }
    }

    /// <summary>Batch classroom-name resolution for enriching file/other listings by classroom id.</summary>
    [HttpPost("names")]
    [ProducesResponseType(typeof(IReadOnlyList<ClassroomNameDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetNames([FromBody] ClassroomIdsRequest request, CancellationToken ct)
    {
        var names = await _classroomRepository.GetNamesByIdsAsync(request?.Ids ?? Array.Empty<Guid>(), ct);
        return Ok(names.Select(n => new ClassroomNameDto(n.Id, n.Name)).ToList());
    }
}

public sealed record InternalCreateClassroomRequest(Guid TeacherId, string Name, string Description);
public sealed record InternalUpdateClassroomRequest(string Name, string Description, long Version);
public sealed record InternalChangeTeacherRequest(Guid NewTeacherId, long Version);
public sealed record InternalAddMemberRequest(Guid StudentId);
public sealed record InternalDeleteClassroomRequest(string Reason);
public sealed record ClassroomIdsRequest(IReadOnlyCollection<Guid> Ids);
public sealed record ClassroomNameDto(Guid Id, string Name);
