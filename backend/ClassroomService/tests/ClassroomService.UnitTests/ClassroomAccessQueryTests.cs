using ClassroomService.Application.Abstractions;
using ClassroomService.Application.Services;
using ClassroomService.Domain.Entities;

namespace ClassroomService.UnitTests;

/// <summary>
/// The roster question asked one person at a time, for a service that has no roster — test-plan
/// G-02, this half of it.
///
/// StreamingService mints the LiveKit join token, which IS entry to the media room: once LiveKit
/// holds it our code is never consulted again. It knows a stream's classroom and its teacher and
/// nothing about who is enrolled, so before this route existed the only checks it could make were
/// "the stream exists" and "the stream is Live" — and any account in the platform could name any
/// live session and be handed a token for it.
///
/// What has to hold here is narrow and easy to get wrong in a way nothing would notice: **this
/// answer must mean the same thing as the one every ClassroomService endpoint already enforces.**
/// If `ClassroomAccess.EnsureMemberAsync` counts the teacher as a member and this does not, a
/// teacher is refused their own lecture; if this counts somebody that one refuses, the remote
/// answer is more generous than the local rule and nothing in either service would report the
/// disagreement. The last test in this file is the one that pins them together.
/// </summary>
public sealed class ClassroomAccessQueryTests
{
    private static readonly Guid ClassroomId = Guid.NewGuid();
    private static readonly Guid TeacherId = Guid.NewGuid();
    private static readonly Guid StudentId = Guid.NewGuid();
    private static readonly Guid OutsiderId = Guid.NewGuid();

    private static (ClassroomMemberAdminService Service, FakeClassroomRepository Classrooms, FakeMembershipRepository Members)
        Build()
    {
        var classrooms = new FakeClassroomRepository();
        classrooms.Seed(new Classroom { Id = ClassroomId, Name = "Physics", TeacherId = TeacherId });
        var members = new FakeMembershipRepository();
        return (new ClassroomMemberAdminService(classrooms, members), classrooms, members);
    }

    [Fact]
    public async Task An_enrolled_student_is_a_member_and_not_the_teacher()
    {
        var (service, _, members) = Build();
        members.Enroll(ClassroomId, StudentId);

        var access = await service.GetAccessAsync(ClassroomId, StudentId);

        Assert.NotNull(access);
        Assert.True(access!.IsMember);
        Assert.False(access.IsTeacher);
    }

    [Fact]
    public async Task The_teacher_is_a_member_without_being_enrolled()
    {
        // The distinction that makes both flags necessary. A teacher is not in the membership
        // table, and a rule that only consulted that table would lock them out of their own class.
        var (service, _, _) = Build();

        var access = await service.GetAccessAsync(ClassroomId, TeacherId);

        Assert.NotNull(access);
        Assert.True(access!.IsMember);
        Assert.True(access.IsTeacher);
    }

    [Fact]
    public async Task A_stranger_is_neither()
    {
        var (service, _, members) = Build();
        members.Enroll(ClassroomId, StudentId);

        var access = await service.GetAccessAsync(ClassroomId, OutsiderId);

        Assert.NotNull(access);
        Assert.False(access!.IsMember);
        Assert.False(access.IsTeacher);
    }

    [Fact]
    public async Task Enrolment_in_a_different_classroom_does_not_carry_over()
    {
        // The scoping. Every caller of this route passes a classroom id it read off a stream row,
        // so an answer that ignored the classroom would admit every enrolled student in the
        // platform to every lecture in it.
        var (service, classrooms, members) = Build();
        var other = Guid.NewGuid();
        classrooms.Seed(new Classroom { Id = other, Name = "Chemistry", TeacherId = Guid.NewGuid() });
        members.Enroll(other, StudentId);

        var access = await service.GetAccessAsync(ClassroomId, StudentId);

        Assert.False(access!.IsMember);
    }

    [Fact]
    public async Task Teaching_a_different_classroom_does_not_carry_over()
    {
        // The same, for the flag that grants a microphone.
        var (service, classrooms, _) = Build();
        var otherTeacher = Guid.NewGuid();
        classrooms.Seed(new Classroom { Id = Guid.NewGuid(), Name = "Chemistry", TeacherId = otherTeacher });

        var access = await service.GetAccessAsync(ClassroomId, otherTeacher);

        Assert.False(access!.IsMember);
        Assert.False(access.IsTeacher);
    }

    [Fact]
    public async Task An_unknown_classroom_answers_null_rather_than_a_negative()
    {
        // Null, not `IsMember: false`, so the controller can answer 404 rather than "no". The
        // caller collapses the two anyway — both refuse — but a stream naming a classroom that no
        // longer exists is a different operational problem from a student who is not enrolled, and
        // the logs on both sides say which one it was.
        var (service, _, _) = Build();

        Assert.Null(await service.GetAccessAsync(Guid.NewGuid(), StudentId));
    }

    [Fact]
    public async Task The_controller_turns_an_unknown_classroom_into_404_rather_than_a_yes()
    {
        // The service returns null and the controller decides what that means, and mutation M10
        // showed that decision was untested: making it answer `Ok(IsMember: true)` broke nothing.
        // That is the fail-OPEN direction, on the one route whose answer decides entry to a live
        // lecture — and a stream naming a deleted classroom is exactly when it would fire.
        var (service, _, _) = Build();
        var controller = new ClassroomService.Presentation.Controllers.InternalClassroomsController(
            classrooms: null!, deletion: null!, members: service, classroomRepository: null!);

        var missing = await controller.GetAccess(Guid.NewGuid(), StudentId, default);
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(missing);

        // And the vacuum guard: a controller that answered 404 for everything would also pass.
        var known = await controller.GetAccess(ClassroomId, TeacherId, default);
        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(known);
        Assert.True(Assert.IsType<Application.DTOs.Membership.ClassroomAccessResult>(ok.Value).IsMember);
    }

    // --- the wire between the two services ------------------------------------------------------

    [Fact]
    public void The_route_this_service_serves_is_the_one_StreamingService_calls()
    {
        // Two repositories' worth of code either side of a URL string, and nothing compiles across
        // it. A renamed segment here is not a build error there — it is a 404, which
        // ClassroomInternalClient correctly turns into "not a member", which refuses every join in
        // the platform with a log line about an unknown classroom. Fails closed, and therefore
        // fails silently until somebody tries to attend a lecture.
        //
        // Same reasoning as MediaSettingsBrowserContractTests reading the front-end's TypeScript:
        // the side that OWNS the contract holds the test.
        var client = File.ReadAllText(Path.Combine(
            BackendRoot(), "StreamingService", "src", "StreamingService.Infrastructure",
            "Services", "ClassroomInternalClient.cs"));

        Assert.Contains("api/internal/classrooms/{classroomId}/access/{userId}", client);

        var controller = File.ReadAllText(Path.Combine(
            RepoServiceRoot(), "src", "ClassroomService.Presentation", "Controllers",
            "InternalClassroomsController.cs"));

        Assert.Contains("[HttpGet(\"{id:guid}/access/{userId:guid}\")]", controller);
        Assert.Contains("[Route(\"api/internal/classrooms\")]", controller);
    }

    [Fact]
    public void The_fields_this_service_sends_are_the_ones_StreamingService_reads()
    {
        // The body half. A record property renamed here still serializes; over there it silently
        // deserializes to `false`, and every join is refused for a reason no log explains, because
        // from the client's point of view the classroom simply said no.
        var client = File.ReadAllText(Path.Combine(
            BackendRoot(), "StreamingService", "src", "StreamingService.Infrastructure",
            "Services", "ClassroomInternalClient.cs"));

        foreach (var field in new[] { "isMember", "isTeacher" })
        {
            Assert.Contains($"JsonPropertyName(\"{field}\")", client);
        }

        // And that those are the names this service actually puts on the wire. ASP.NET's default
        // camelCase policy turns IsMember into isMember, so the two agree today by convention
        // rather than by declaration — which is exactly the kind of agreement that ends when
        // somebody sets a different naming policy for an unrelated reason.
        var properties = typeof(Application.DTOs.Membership.ClassroomAccessResult)
            .GetProperties()
            .Select(p => p.Name)
            .ToList();

        Assert.Contains("IsMember", properties);
        Assert.Contains("IsTeacher", properties);
    }

    private static string RepoServiceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, "src", "ClassroomService.Application")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string BackendRoot() => new DirectoryInfo(RepoServiceRoot()).Parent!.FullName;

    [Fact]
    public async Task It_agrees_with_the_rule_every_other_endpoint_enforces()
    {
        // The one that matters. Two spellings of "member" is the §11.7 defect at a distance: this
        // one is consulted over HTTP by another service, and a disagreement would surface as a
        // student who can read a classroom's files but not enter its lecture, or the reverse.
        //
        // Driven over all four people rather than asserted once, so a rule that agreed on the easy
        // cases and not on the teacher — which is the case they differ on — cannot pass.
        var (service, classrooms, members) = Build();
        members.Enroll(ClassroomId, StudentId);

        foreach (var person in new[] { TeacherId, StudentId, OutsiderId, Guid.NewGuid() })
        {
            var remote = (await service.GetAccessAsync(ClassroomId, person))!.IsMember;

            var local = true;
            try
            {
                await ClassroomAccess.EnsureMemberAsync(classrooms, members, ClassroomId, person, default);
            }
            catch (Application.Exceptions.ForbiddenAccessException)
            {
                local = false;
            }

            Assert.True(
                remote == local,
                $"GetAccessAsync says IsMember={remote} for {person} while ClassroomAccess says {local}. "
                + "Another service is now deciding entry to a lecture on a different rule from the "
                + "one this service enforces on everything else.");
        }
    }
}
