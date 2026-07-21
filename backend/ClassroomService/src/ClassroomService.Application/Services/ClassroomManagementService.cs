using AutoMapper;
using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs;
using ClassroomService.Application.DTOs.Classroom;
using ClassroomService.Application.Exceptions;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Services;

public sealed class ClassroomManagementService : IClassroomManagementService
{
    private readonly IClassroomRepository _classroomRepository;
    private readonly IMapper _mapper;

    public ClassroomManagementService(IClassroomRepository classroomRepository, IMapper mapper)
    {
        _classroomRepository = classroomRepository;
        _mapper = mapper;
    }

    public async Task<PagedResult<ClassroomResponse>> GetAllAsync(int page, int pageSize, CancellationToken ct)
    {
        var (classrooms, totalCount) = await _classroomRepository.GetPagedAsync(page, pageSize, ct);

        var mappedItems = _mapper.Map<List<ClassroomResponse>>(classrooms);

        return new PagedResult<ClassroomResponse>(mappedItems, totalCount, page, pageSize);
    }

    public async Task<Guid> CreateAsync(Guid teacherId, CreateClassroomRequest request, CancellationToken ct)
    {
        var classroom = _mapper.Map<Classroom>(request);
        classroom.TeacherId = teacherId;

        await _classroomRepository.AddAsync(classroom, ct);
        await _classroomRepository.SaveChangesAsync(ct);
        return classroom.Id;
    }

    public async Task<ClassroomResponse> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var classroom = await _classroomRepository.GetWithDetailsAsync(id, ct);
        if (classroom == null) throw new KeyNotFoundException("Classroom not found.");

        return _mapper.Map<ClassroomResponse>(classroom);
    }

    public async Task<IEnumerable<ClassroomResponse>> GetByTeacherIdAsync(Guid teacherId, CancellationToken ct)
    {
        var classrooms = await _classroomRepository.GetByTeacherIdAsync(teacherId, ct);
        return _mapper.Map<IEnumerable<ClassroomResponse>>(classrooms);
    }

    public async Task<IEnumerable<ClassroomResponse>> GetEnrolledClassroomsAsync(Guid studentId, CancellationToken ct)
    {
        var classrooms = await _classroomRepository.GetEnrolledClassroomsAsync(studentId, ct);
        return _mapper.Map<IEnumerable<ClassroomResponse>>(classrooms);
    }

    public async Task UpdateAsync(Guid classroomId, Guid teacherId, UpdateClassroomRequest request, CancellationToken ct)
    {
        var classroom = await _classroomRepository.GetByIdAsync(classroomId, ct);
        if (classroom == null || classroom.TeacherId != teacherId)
            throw new UnauthorizedAccessException("Not authorized to update this classroom.");

        classroom.Name = request.Name;
        classroom.Description = request.Description;

        await _classroomRepository.UpdateAsync(classroom, ct);
        await _classroomRepository.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid classroomId, Guid teacherId, CancellationToken ct)
    {
        var classroom = await _classroomRepository.GetByIdAsync(classroomId, ct);
        if (classroom == null || classroom.TeacherId != teacherId)
            throw new UnauthorizedAccessException("Not authorized to delete this classroom.");

        await _classroomRepository.DeleteAsync(classroomId, ct);
        await _classroomRepository.SaveChangesAsync(ct);
    }

    public async Task<PagedResult<AdminClassroomResponse>> GetAdminPagedAsync(
        int page, int pageSize, string? search, Guid? teacherId, CancellationToken ct = default)
    {
        var (items, totalCount) = await _classroomRepository.GetAdminPagedAsync(page, pageSize, search, teacherId, ct);
        return new PagedResult<AdminClassroomResponse>(items, totalCount, page, pageSize);
    }

    public Task<AdminClassroomResponse?> GetAdminByIdAsync(Guid id, CancellationToken ct = default)
        => _classroomRepository.GetAdminByIdAsync(id, ct);

    public async Task AdminUpdateAsync(Guid id, UpdateClassroomRequest request, long expectedVersion, CancellationToken ct = default)
    {
        // A stale version surfaces as DbUpdateConcurrencyException (6أ), handled by the caller.
        var found = await _classroomRepository.UpdateWithConcurrencyAsync(
            id, request.Name, request.Description, expectedVersion, ct);

        // Alternate path 5ج: the classroom to edit does not exist.
        if (!found)
        {
            throw new KeyNotFoundException("Classroom not found.");
        }
    }

    public async Task<ChangeTeacherResult> ChangeTeacherAsync(
        Guid id, Guid newTeacherId, long expectedVersion, CancellationToken ct = default)
    {
        // 3أ: the classroom must exist.
        var info = await _classroomRepository.GetTeacherInfoAsync(id, ct);
        if (info is null)
        {
            throw new KeyNotFoundException("Classroom not found.");
        }

        // 3ب: refuse to move ownership while a lecture is live.
        if (await _classroomRepository.HasLiveSessionAsync(id, ct))
        {
            throw new ConflictException(
                "The classroom has a live session. End the session before changing its teacher.");
        }

        // 4ب: the new teacher already owns the classroom — treat as a no-op, no change, no notify.
        if (info.TeacherId == newTeacherId)
        {
            return new ChangeTeacherResult(false, info.TeacherId, newTeacherId, info.Name);
        }

        // Step 5: transfer ownership. A stale version surfaces as ConflictException (409).
        var found = await _classroomRepository.ChangeTeacherWithConcurrencyAsync(id, newTeacherId, expectedVersion, ct);
        if (!found)
        {
            // The classroom was deleted between the read above and the write (a rare race).
            throw new KeyNotFoundException("Classroom not found.");
        }

        return new ChangeTeacherResult(true, info.TeacherId, newTeacherId, info.Name);
    }
}