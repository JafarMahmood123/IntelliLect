using AutoMapper;
using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Membership;
using ClassroomService.Application.Exceptions;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Services;

public sealed class MembershipService : IMembershipService
{
    private readonly IMembershipRepository _membershipRepository;
    private readonly IClassroomRepository _classroomRepository;
    private readonly IMapper _mapper;

    public MembershipService(
        IMembershipRepository membershipRepository,
        IClassroomRepository classroomRepository,
        IMapper mapper)
    {
        _membershipRepository = membershipRepository;
        _classroomRepository = classroomRepository;
        _mapper = mapper;
    }

    public async Task EnrollStudentAsync(Guid classroomId, Guid studentId, CancellationToken ct)
    {
        var classroom = await _classroomRepository.GetByIdAsync(classroomId, ct);
        if (classroom == null)
            throw new KeyNotFoundException("Classroom not found.");

        var isEnrolled = await _membershipRepository.IsEnrolledAsync(classroomId, studentId, ct);
        if (isEnrolled)
            throw new ConflictException("Student is already enrolled in this classroom.");

        var membership = new ClassroomMembership
        {
            ClassroomId = classroomId,
            StudentId = studentId,
            JoinedAtUtc = DateTime.UtcNow
        };

        await _membershipRepository.AddAsync(membership, ct);
        await _membershipRepository.SaveChangesAsync(ct);
    }

    public async Task RemoveStudentAsync(Guid classroomId, Guid teacherId, Guid studentId, CancellationToken ct)
    {
        var classroom = await _classroomRepository.GetByIdAsync(classroomId, ct);
        if (classroom == null)
            throw new KeyNotFoundException("Classroom not found.");

        if (classroom.TeacherId != teacherId)
            throw new UnauthorizedAccessException("Only the teacher can remove students from this classroom.");

        var membership = await _membershipRepository.GetMembershipAsync(classroomId, studentId, ct);
        if (membership == null)
            throw new KeyNotFoundException("Membership record not found.");

        await _membershipRepository.DeleteAsync(membership.Id);
        await _membershipRepository.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The classroom's roster, for its own members.
    ///
    /// It took no caller, so any authenticated user could read any classroom's roster by id — and
    /// <c>MemberResponse</c> carries each student's full name, so this was a personal-data
    /// disclosure rather than a metadata one. <see cref="RemoveStudentAsync"/> next door checks
    /// ownership before it even looks a membership up, precisely so a classroom id cannot be used
    /// to probe who is enrolled; the listing answered the same question outright.
    ///
    /// Gated on membership rather than on ownership, matching the other classroom reads: a student
    /// seeing their own classmates is ordinary, and the alternative reading — teacher-only — is a
    /// product decision this does not make on its own. Nothing in the front-end calls this route
    /// today; the super-admin roster view goes through UMS.
    /// </summary>
    public async Task<IEnumerable<MemberResponse>> GetClassroomMembersAsync(
        Guid classroomId, Guid requestingUserId, CancellationToken ct)
    {
        await ClassroomAccess.EnsureMemberAsync(
            _classroomRepository, _membershipRepository, classroomId, requestingUserId, ct);

        var memberships = await _membershipRepository.GetMembersWithDetailsAsync(classroomId, ct);

        return _mapper.Map<IEnumerable<MemberResponse>>(memberships);
    }
}