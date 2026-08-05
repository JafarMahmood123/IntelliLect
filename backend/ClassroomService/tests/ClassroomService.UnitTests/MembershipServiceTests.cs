using ClassroomService.Application.Exceptions;
using ClassroomService.Application.Services;
using ClassroomService.Domain.Entities;

namespace ClassroomService.UnitTests;

/// <summary>
/// Enrolment and removal — who is in a classroom, which is the input to almost everything else
/// here. Membership decides who may join the session, who is counted in the tracking summary, who
/// is ranked, and whose answers are graded. It had no tests at all.
///
/// The rule worth protecting is the one on removal: only the classroom's own teacher may take a
/// student out. Without it any teacher with a classroom id could unenrol another teacher's
/// students — and to the student that reads as having been thrown out of a course, with no record
/// anywhere of who did it.
/// </summary>
public sealed class MembershipServiceTests
{
    private static readonly Guid ClassroomId = Guid.NewGuid();
    private static readonly Guid TeacherId = Guid.NewGuid();
    private static readonly Guid StudentId = Guid.NewGuid();

    private sealed record Harness(
        MembershipService Service,
        FakeMembershipRepository Members,
        FakeClassroomRepository Classrooms);

    private static Harness Build(bool withClassroom = true)
    {
        var classrooms = new FakeClassroomRepository();
        if (withClassroom)
        {
            classrooms.Seed(new Classroom { Id = ClassroomId, TeacherId = TeacherId, Name = "Physics" });
        }

        var members = new FakeMembershipRepository();
        var service = new MembershipService(members, classrooms, TestMapper.Create());
        return new Harness(service, members, classrooms);
    }

    // --- enrolment ----------------------------------------------------------------

    [Fact]
    public async Task Enrolling_records_the_student_against_the_classroom()
    {
        var h = Build();

        await h.Service.EnrollStudentAsync(ClassroomId, StudentId, default);

        Assert.True(await h.Members.IsEnrolledAsync(ClassroomId, StudentId));
        var membership = Assert.Single(h.Members.All);
        Assert.Equal(ClassroomId, membership.ClassroomId);
        Assert.Equal(StudentId, membership.StudentId);
    }

    [Fact]
    public async Task Enrolling_persists_rather_than_only_tracking_the_change()
    {
        // The write and the save are separate calls, and an enrolment that is added but never
        // saved disappears at the end of the request with nothing reporting a failure.
        var h = Build();

        await h.Service.EnrollStudentAsync(ClassroomId, StudentId, default);

        Assert.Equal(1, h.Members.SaveChangesCount);
    }

    [Fact]
    public async Task Enrolling_stamps_when_the_student_joined()
    {
        // The join date is shown on the roster and is the only record of when someone entered a
        // course; a default DateTime would render as year 1.
        var before = DateTime.UtcNow;
        var h = Build();

        await h.Service.EnrollStudentAsync(ClassroomId, StudentId, default);

        var membership = Assert.Single(h.Members.All);
        Assert.InRange(membership.JoinedAtUtc, before, DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task Enrolling_into_a_classroom_that_does_not_exist_is_refused()
    {
        // Otherwise a membership row exists for a classroom nobody can open — invisible, and it
        // outlives whatever typo created it.
        var h = Build(withClassroom: false);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => h.Service.EnrollStudentAsync(ClassroomId, StudentId, default));

        Assert.Empty(h.Members.All);
    }

    [Fact]
    public async Task Enrolling_the_same_student_twice_is_refused_rather_than_duplicated()
    {
        // A second row for the same student would double them in the roster count and, worse, in
        // the tracking totals the ranking is computed from.
        var h = Build();
        await h.Service.EnrollStudentAsync(ClassroomId, StudentId, default);

        await Assert.ThrowsAsync<ConflictException>(
            () => h.Service.EnrollStudentAsync(ClassroomId, StudentId, default));

        Assert.Single(h.Members.All);
    }

    [Fact]
    public async Task Enrolment_is_scoped_to_one_classroom()
    {
        // The uniqueness check is on the pair, not on the student: being in Physics must not stop
        // the same person joining Chemistry.
        var otherClassroom = Guid.NewGuid();
        var h = Build();
        h.Classrooms.Seed(new Classroom { Id = otherClassroom, TeacherId = TeacherId, Name = "Chemistry" });

        await h.Service.EnrollStudentAsync(ClassroomId, StudentId, default);
        await h.Service.EnrollStudentAsync(otherClassroom, StudentId, default);

        Assert.Equal(2, h.Members.All.Count);
    }

    // --- removal ------------------------------------------------------------------

    [Fact]
    public async Task The_classroom_s_teacher_can_remove_a_student()
    {
        var h = Build();
        await h.Service.EnrollStudentAsync(ClassroomId, StudentId, default);

        await h.Service.RemoveStudentAsync(ClassroomId, TeacherId, StudentId, default);

        Assert.False(await h.Members.IsEnrolledAsync(ClassroomId, StudentId));
        Assert.Empty(h.Members.All);
    }

    [Fact]
    public async Task Another_teacher_cannot_remove_a_student_from_a_classroom_they_do_not_own()
    {
        // The authorization rule this service exists to hold. A classroom id is not a secret — it
        // is in every URL the students use — so ownership has to be checked here rather than
        // assumed from the caller having one.
        var h = Build();
        await h.Service.EnrollStudentAsync(ClassroomId, StudentId, default);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.Service.RemoveStudentAsync(ClassroomId, Guid.NewGuid(), StudentId, default));

        Assert.True(await h.Members.IsEnrolledAsync(ClassroomId, StudentId));
    }

    [Fact]
    public async Task Ownership_is_checked_before_the_membership_is_looked_up()
    {
        // Ordering matters: if the missing-membership check ran first, the two failures would be
        // distinguishable, and an outsider could probe whether a given student is in a classroom
        // by which error comes back.
        var h = Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.Service.RemoveStudentAsync(ClassroomId, Guid.NewGuid(), StudentId, default));
    }

    [Fact]
    public async Task Removing_from_a_classroom_that_does_not_exist_is_refused()
    {
        var h = Build(withClassroom: false);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => h.Service.RemoveStudentAsync(ClassroomId, TeacherId, StudentId, default));
    }

    [Fact]
    public async Task Removing_a_student_who_was_never_enrolled_is_refused()
    {
        // Reported rather than silently succeeding: the teacher pressed remove on someone, and
        // "done" when nothing happened hides a stale roster on the screen.
        var h = Build();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => h.Service.RemoveStudentAsync(ClassroomId, TeacherId, StudentId, default));
    }

    [Fact]
    public async Task Removing_one_student_leaves_the_others_enrolled()
    {
        // Deleting by membership id rather than by classroom is what makes this true; a delete
        // scoped to the classroom would empty the roster.
        var other = Guid.NewGuid();
        var h = Build();
        await h.Service.EnrollStudentAsync(ClassroomId, StudentId, default);
        await h.Service.EnrollStudentAsync(ClassroomId, other, default);

        await h.Service.RemoveStudentAsync(ClassroomId, TeacherId, StudentId, default);

        Assert.False(await h.Members.IsEnrolledAsync(ClassroomId, StudentId));
        Assert.True(await h.Members.IsEnrolledAsync(ClassroomId, other));
    }

    [Fact]
    public async Task Removal_persists_rather_than_only_tracking_the_change()
    {
        var h = Build();
        await h.Service.EnrollStudentAsync(ClassroomId, StudentId, default);
        var savesAfterEnrolment = h.Members.SaveChangesCount;

        await h.Service.RemoveStudentAsync(ClassroomId, TeacherId, StudentId, default);

        Assert.Equal(savesAfterEnrolment + 1, h.Members.SaveChangesCount);
    }

    // --- the roster ---------------------------------------------------------------

    [Fact]
    public async Task The_roster_lists_everyone_enrolled_in_that_classroom_only()
    {
        var otherClassroom = Guid.NewGuid();
        var otherStudent = Guid.NewGuid();
        var h = Build();
        h.Classrooms.Seed(new Classroom { Id = otherClassroom, TeacherId = TeacherId, Name = "Chemistry" });
        await h.Service.EnrollStudentAsync(ClassroomId, StudentId, default);
        await h.Service.EnrollStudentAsync(otherClassroom, otherStudent, default);

        var members = await h.Service.GetClassroomMembersAsync(ClassroomId, default);

        var row = Assert.Single(members);
        Assert.Equal(StudentId, row.StudentId);
    }

    [Fact]
    public async Task The_roster_of_an_empty_classroom_is_empty_rather_than_an_error()
    {
        // A classroom with nobody in it yet is the normal state on the day it is created.
        var h = Build();

        Assert.Empty(await h.Service.GetClassroomMembersAsync(ClassroomId, default));
    }
}
