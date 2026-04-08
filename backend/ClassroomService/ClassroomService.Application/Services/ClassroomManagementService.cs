using AutoMapper;
using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Classroom;
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
}