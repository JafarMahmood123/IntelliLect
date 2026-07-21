using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Membership;
using ClassroomService.Application.Exceptions;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Services;

public sealed class ClassroomMemberAdminService : IClassroomMemberAdminService
{
    private readonly IClassroomRepository _classroomRepository;
    private readonly IMembershipRepository _membershipRepository;

    public ClassroomMemberAdminService(
        IClassroomRepository classroomRepository,
        IMembershipRepository membershipRepository)
    {
        _classroomRepository = classroomRepository;
        _membershipRepository = membershipRepository;
    }

    public async Task<ClassroomMembersResult> GetMembersAsync(Guid classroomId, CancellationToken ct = default)
    {
        var info = await RequireClassroomAsync(classroomId, ct); // 5أ

        var memberships = await _membershipRepository.GetMembersWithDetailsAsync(classroomId, ct);
        var students = memberships
            .Select(m => new ClassroomMemberRow(m.StudentId, m.JoinedAtUtc))
            .ToList();

        return new ClassroomMembersResult(classroomId, info.Name, info.TeacherId, students);
    }

    public async Task<MemberMutationResult> AddMemberAsync(Guid classroomId, Guid studentId, CancellationToken ct = default)
    {
        var info = await RequireClassroomAsync(classroomId, ct); // 5أ

        // 5ج: already a member (as a student, or as the owning teacher) -> no-op, no duplicate row.
        if (studentId == info.TeacherId
            || await _membershipRepository.IsEnrolledAsync(classroomId, studentId, ct))
        {
            return new MemberMutationResult(false, classroomId, info.Name, studentId);
        }

        // Step 6: enroll (mirrors the normal join flow's membership creation).
        await _membershipRepository.AddAsync(
            new ClassroomMembership
            {
                ClassroomId = classroomId,
                StudentId = studentId,
                JoinedAtUtc = DateTime.UtcNow,
            },
            ct);
        await _membershipRepository.SaveChangesAsync(ct);

        return new MemberMutationResult(true, classroomId, info.Name, studentId);
    }

    public async Task<MemberMutationResult> RemoveMemberAsync(Guid classroomId, Guid studentId, CancellationToken ct = default)
    {
        var info = await RequireClassroomAsync(classroomId, ct); // 5أ

        // 5هـ: the owning teacher is not a removable member here — teacher changes go through the
        // separate ownership-transfer use-case.
        if (studentId == info.TeacherId)
        {
            throw new ConflictException(
                "The classroom teacher cannot be removed here. Use teacher reassignment to change the owner.");
        }

        // 5د: the membership must exist.
        var membership = await _membershipRepository.GetMembershipAsync(classroomId, studentId, ct);
        if (membership is null)
        {
            throw new KeyNotFoundException("Membership not found.");
        }

        // Step 6: remove the membership, ending the student's access to the classroom.
        await _membershipRepository.DeleteAsync(membership.Id, ct);
        await _membershipRepository.SaveChangesAsync(ct);

        return new MemberMutationResult(true, classroomId, info.Name, studentId);
    }

    private async Task<DTOs.Classroom.ClassroomTeacherInfo> RequireClassroomAsync(Guid classroomId, CancellationToken ct)
    {
        var info = await _classroomRepository.GetTeacherInfoAsync(classroomId, ct);
        if (info is null)
        {
            throw new KeyNotFoundException("Classroom not found."); // 5أ
        }

        return info;
    }
}
