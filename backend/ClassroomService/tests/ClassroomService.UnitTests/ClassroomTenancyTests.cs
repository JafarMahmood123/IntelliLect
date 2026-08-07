using System.Reflection;
using System.Text.RegularExpressions;
using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Session;
using ClassroomService.Application.Exceptions;
using ClassroomService.Application.Services;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
using ClassroomService.Presentation.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassroomService.UnitTests;

/// <summary>
/// A classroom id in the URL is not permission to read the classroom — test-plan B-04, B-05, B-07.
///
/// <see cref="PublicRouteAuthorizationTests"/> asks whether a route is authenticated and whether a
/// mutation names a role. Both questions were answered correctly by five endpoints that were still
/// wide open, because neither question is the one that matters once you are through the door:
///
/// - **A role is not a tenancy check.** <c>[Authorize(Roles = "Teacher")]</c> proves the caller is
///   *a* teacher, never that this classroom is theirs. Two session routes rested on exactly that.
/// - **A GET was excused on an assumption.** That rule's own comment reads "a GET can be left to a
///   membership check in the service layer — a student reading their own classroom's files is
///   legitimate, and only the service knows whether they are enrolled." It names the file listing
///   as its example. The file listing did not have that check.
///
/// So the question this file asks is the third one: does the decision reach a place that can see
/// **which** classroom and **who** is asking? That is answerable without a container, because it is
/// a question about the service layer's signatures — a method handed a <c>classroomId</c> and no
/// caller cannot possibly be scoping anything.
/// </summary>
public sealed class ClassroomTenancyTests
{
    // --- the rule over the service layer ------------------------------------------------------

    /// <summary>
    /// Parameter names that carry the authenticated caller. Names, because that is what a signature
    /// exposes — the alternative is a marker attribute nobody would remember to apply, which is the
    /// same class of omission this rule exists to catch.
    /// </summary>
    private static readonly string[] CallerNames =
        ["requestingUserId", "userId", "teacherId", "studentId", "uploaderId"];

    /// <summary>
    /// Methods that take a classroom id and legitimately no caller, each with the reason.
    ///
    /// Empty on purpose. Every browser-facing method that scopes to a classroom currently needs the
    /// caller, and an entry appearing here should be an argument somebody has to make in writing.
    /// The list is kept — and checked in both directions below — so that argument has somewhere to
    /// go other than a silently missing parameter.
    /// </summary>
    private static readonly Dictionary<string, string> NoCallerNeeded = new();

    /// <summary>
    /// The controllers whose services this rule covers: the browser-facing ones. Internal
    /// controllers are excluded because their caller is another service holding the shared secret,
    /// not a user — <see cref="InternalSecretGuardTests"/> is the rule for those.
    /// </summary>
    private static List<Type> BrowserControllers()
        => typeof(InternalSecretAttribute).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .Where(t => !t.Name.StartsWith("Internal", StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// The application-service interfaces those controllers inject. Derived from the controllers
    /// rather than listed, so a new browser-facing controller pulls its services under the rule by
    /// existing — the failure mode being guarded against is a new endpoint, and a hand-written list
    /// of services is exactly what a new endpoint would not be on.
    /// </summary>
    private static List<Type> ClassroomScopedServices()
        => BrowserControllers()
            .SelectMany(c => c.GetConstructors())
            .SelectMany(ctor => ctor.GetParameters())
            .Select(p => p.ParameterType)
            .Where(t => t.IsInterface && t.Assembly == typeof(IClassroomFileService).Assembly)
            .Distinct()
            .ToList();

    private static bool TakesClassroomId(MethodInfo method)
        => method.GetParameters().Any(p => p.ParameterType == typeof(Guid) && p.Name == "classroomId");

    private static bool TakesCaller(MethodInfo method)
        => method.GetParameters().Any(p => p.ParameterType == typeof(Guid) && CallerNames.Contains(p.Name));

    private static IEnumerable<(Type Service, MethodInfo Method)> ClassroomScopedMethods()
        => ClassroomScopedServices()
            .SelectMany(s => s.GetMethods().Select(m => (Service: s, Method: m)))
            .Where(x => TakesClassroomId(x.Method));

    [Fact]
    public void Every_classroom_scoped_service_method_is_told_who_is_asking()
    {
        var anonymous = ClassroomScopedMethods()
            .Where(x => !TakesCaller(x.Method))
            .Select(x => $"{x.Service.Name}.{x.Method.Name}")
            .Where(name => !NoCallerNeeded.ContainsKey(name))
            .OrderBy(name => name)
            .ToList();

        Assert.True(
            anonymous.Count == 0,
            "These methods scope to a classroom but never receive the caller, so they cannot tell a "
            + "member from a stranger and the classroom id in the URL is the whole of the access "
            + "control: " + string.Join(", ", anonymous));
    }

    [Fact]
    public void No_exemption_names_a_method_that_no_longer_exists()
    {
        // An entry for a renamed method excuses nothing today and silently excuses whatever takes
        // the name tomorrow — the same failure the internal-secret and [Authorize] lists guard.
        var present = ClassroomScopedMethods()
            .Select(x => $"{x.Service.Name}.{x.Method.Name}")
            .ToHashSet();
        var stale = NoCallerNeeded.Keys.Where(name => !present.Contains(name)).ToList();

        Assert.True(stale.Count == 0, $"Exempted but no such method: {string.Join(", ", stale)}");
    }

    [Fact]
    public void No_exemption_names_a_method_that_has_since_gained_a_caller()
    {
        // The other direction. A method that was exempted and has since been given the caller
        // should come off the list, or the list stops meaning "these deliberately do without one".
        var unnecessary = ClassroomScopedMethods()
            .Where(x => NoCallerNeeded.ContainsKey($"{x.Service.Name}.{x.Method.Name}"))
            .Where(x => TakesCaller(x.Method))
            .Select(x => $"{x.Service.Name}.{x.Method.Name}")
            .ToList();

        Assert.True(
            unnecessary.Count == 0,
            $"Exempted but now takes a caller — remove from the list: {string.Join(", ", unnecessary)}");
    }

    [Fact]
    public void There_are_services_and_classroom_scoped_methods_to_check()
    {
        // The vacuum guard. Every rule above is a query over reflection, and a query that matches
        // nothing passes loudly while proving nothing — a renamed assembly, a controller base class
        // that stops deriving from ControllerBase, a `classroomId` renamed to `id`, and this file
        // becomes decorative without a single failure.
        var services = ClassroomScopedServices();
        var methods = ClassroomScopedMethods().ToList();

        Assert.True(services.Count >= 6, $"Only found {services.Count} browser-facing services.");
        Assert.True(methods.Count >= 20, $"Only found {methods.Count} classroom-scoped methods.");

        // And that the caller-name list itself matches something: a typo in every entry would make
        // `TakesCaller` return false everywhere, which fails loudly — but a typo in the entries
        // that happen to be unused would not, and the list would quietly shrink.
        foreach (var name in CallerNames)
        {
            Assert.True(
                ClassroomScopedMethods().Any(x => x.Method.GetParameters().Any(p => p.Name == name)),
                $"No classroom-scoped method has a parameter named '{name}' — dead entry or a typo.");
        }
    }

    // --- the timetable: any authenticated user could read any classroom's ----------------------

    [Fact]
    public async Task A_non_member_cannot_list_a_classrooms_sessions()
    {
        var world = new World();

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => world.Sessions.GetSessionsByClassroomAsync(world.ClassroomId, World.Outsider));
    }

    [Fact]
    public async Task An_enrolled_student_and_the_teacher_can_both_list_them()
    {
        // The vacuum guard on the rule above: gating this on ownership instead of membership would
        // refuse every student, which is most of the people the timetable is for.
        var world = new World();
        world.Enrol(World.Student);

        Assert.Single(await world.Sessions.GetSessionsByClassroomAsync(world.ClassroomId, World.Student));
        Assert.Single(await world.Sessions.GetSessionsByClassroomAsync(world.ClassroomId, World.Teacher));
    }

    [Fact]
    public async Task Listing_the_sessions_of_a_classroom_that_does_not_exist_is_not_found()
    {
        var world = new World();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => world.Sessions.GetSessionsByClassroomAsync(Guid.NewGuid(), World.Teacher));
    }

    // --- scheduling: any teacher could schedule into any classroom ------------------------------

    [Fact]
    public async Task Another_teacher_cannot_schedule_a_session_in_a_classroom_they_do_not_own()
    {
        // The Teacher role was the whole check. Holding it says what kind of account this is; it
        // never said whose classroom this is.
        var world = new World();
        var before = world.SessionRows.All.Count;

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => world.Sessions.CreateSessionAsync(world.ClassroomId, World.OtherTeacher, NewSession()));

        Assert.Equal(before, world.SessionRows.All.Count);
    }

    [Fact]
    public async Task A_student_cannot_schedule_a_session_even_in_their_own_classroom()
    {
        // Membership is not enough here, and using the shared member check would have made it so.
        var world = new World();
        world.Enrol(World.Student);

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => world.Sessions.CreateSessionAsync(world.ClassroomId, World.Student, NewSession()));
    }

    [Fact]
    public async Task The_owning_teacher_schedules_a_session_normally()
    {
        var world = new World();

        var session = await world.Sessions.CreateSessionAsync(world.ClassroomId, World.Teacher, NewSession());

        Assert.Equal(world.ClassroomId, session.ClassroomId);
        Assert.Equal(SessionStatus.Scheduled, session.Status);
        Assert.Contains(world.SessionRows.All, s => s.Id == session.Id);
    }

    // --- starting: any teacher could start any session in the platform --------------------------

    [Fact]
    public async Task Another_teacher_cannot_start_a_session_they_do_not_own()
    {
        // The live one. Starting opens the media room and begins recording if the session was
        // configured for it — done to someone else's class, from a session id.
        var world = new World();

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => world.Sessions.StartSessionAsync(world.ClassroomId, world.ScheduledId, World.OtherTeacher));

        Assert.Equal(SessionStatus.Scheduled, world.Scheduled.Status);
        Assert.Null(world.Streaming.LastCreatedSessionId);
    }

    [Fact]
    public async Task A_session_started_under_the_wrong_classroom_is_not_found()
    {
        // 404 rather than 403, matching EndSessionAsync: a teacher who owns SOME classroom must not
        // be able to use their own id to discover which sessions exist elsewhere.
        //
        // The caller is the OTHER teacher addressing a real session of theirs under a real classroom
        // of the caller's own. Written first with an invented classroom id, which made the mutation
        // that removes the scoping survive: an unknown classroom is 404 from the ownership check
        // too, so the test passed either way and proved nothing about the scoping.
        var world = new World();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => world.Sessions.StartSessionAsync(
                world.OtherClassroomId, world.ScheduledId, World.OtherTeacher));

        Assert.Equal(SessionStatus.Scheduled, world.Scheduled.Status);
        Assert.Null(world.Streaming.LastCreatedSessionId);
    }

    [Fact]
    public async Task Ownership_is_decided_before_the_session_status_is()
    {
        // Ordering, and it is the whole reason the status check moved. A session that is already
        // Live would answer 409 "only scheduled sessions can be started" — which tells a stranger
        // that the session exists AND that a class is running right now. The refusal has to be the
        // same one they would get for a session that was never scheduled at all.
        var world = new World();
        world.Scheduled.Status = SessionStatus.Live;

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => world.Sessions.StartSessionAsync(world.ClassroomId, world.ScheduledId, World.OtherTeacher));
    }

    [Fact]
    public async Task The_owning_teacher_starts_their_own_session()
    {
        var world = new World();

        await world.Sessions.StartSessionAsync(world.ClassroomId, world.ScheduledId, World.Teacher);

        Assert.Equal(SessionStatus.Live, world.Scheduled.Status);
        Assert.Equal(world.ScheduledId, world.Streaming.LastCreatedSessionId);
    }

    // --- the material list: the bytes were gated and the catalogue was not ----------------------

    [Fact]
    public async Task A_non_member_cannot_list_a_classrooms_files()
    {
        var world = new World();

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => world.Files.GetClassroomFilesAsync(world.ClassroomId, World.Outsider, default));
    }

    [Fact]
    public async Task An_enrolled_student_can_list_the_files()
    {
        var world = new World();
        world.Enrol(World.Student);

        Assert.Single(await world.Files.GetClassroomFilesAsync(world.ClassroomId, World.Student, default));
    }

    [Fact]
    public async Task Listing_the_files_of_a_classroom_that_does_not_exist_is_not_found()
    {
        // It used to be 200 with an empty list, which reads to a caller as "that classroom has no
        // material" — an answer about a classroom they were never entitled to ask about.
        var world = new World();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => world.Files.GetClassroomFilesAsync(Guid.NewGuid(), World.Teacher, default));
    }

    // --- the roster: names, to anybody with the classroom id ------------------------------------

    [Fact]
    public async Task A_non_member_cannot_read_a_classrooms_roster()
    {
        var world = new World();

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => world.Members.GetClassroomMembersAsync(world.ClassroomId, World.Outsider, default));
    }

    [Fact]
    public async Task A_classmate_and_the_teacher_can_both_read_it()
    {
        var world = new World();
        world.Enrol(World.Student);

        Assert.Single(await world.Members.GetClassroomMembersAsync(world.ClassroomId, World.Student, default));
        Assert.Single(await world.Members.GetClassroomMembersAsync(world.ClassroomId, World.Teacher, default));
    }

    // --- the shared rule the five private copies became ----------------------------------------

    [Fact]
    public async Task The_shared_member_rule_refuses_a_stranger_and_admits_both_kinds_of_member()
    {
        var world = new World();
        world.Enrol(World.Student);

        await ClassroomAccess.EnsureMemberAsync(
            world.Classrooms, world.Memberships, world.ClassroomId, World.Teacher, default);
        await ClassroomAccess.EnsureMemberAsync(
            world.Classrooms, world.Memberships, world.ClassroomId, World.Student, default);

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => ClassroomAccess.EnsureMemberAsync(
                world.Classrooms, world.Memberships, world.ClassroomId, World.Outsider, default));
    }

    [Fact]
    public async Task The_shared_teacher_rule_refuses_a_member_who_is_not_the_owner()
    {
        // The distinction the five copies could not express, and the one two session routes needed.
        var world = new World();
        world.Enrol(World.Student);

        var classroom = await ClassroomAccess.EnsureTeacherAsync(
            world.Classrooms, world.ClassroomId, World.Teacher, default);
        Assert.Equal(world.ClassroomId, classroom.Id);

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => ClassroomAccess.EnsureTeacherAsync(
                world.Classrooms, world.ClassroomId, World.Student, default));
        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => ClassroomAccess.EnsureTeacherAsync(
                world.Classrooms, world.ClassroomId, World.OtherTeacher, default));
    }

    [Fact]
    public async Task Both_shared_rules_report_an_unknown_classroom_as_missing_rather_than_forbidden()
    {
        var world = new World();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => ClassroomAccess.EnsureMemberAsync(
                world.Classrooms, world.Memberships, Guid.NewGuid(), World.Teacher, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => ClassroomAccess.EnsureTeacherAsync(
                world.Classrooms, Guid.NewGuid(), World.Teacher, default));
    }

    [Fact]
    public void No_service_still_carries_its_own_copy_of_the_membership_rule()
    {
        // Five services each held a byte-identical private EnsureMemberAsync. All five agreed,
        // which is the good case and not a safe one — the next reader to change the rule changes
        // the copy in front of them. §11.7 spent two surviving mutations learning that with two
        // copies of the quiz deadline; this was the same thing five times over.
        //
        // Asserted on the SOURCE, because a delegating one-liner and an inlined duplicate are
        // indistinguishable by reflection, and it is the duplicate body that is the hazard.
        //
        // Matched on the rule's SHAPE — "owner OR enrolled" — and not on its refusal message, which
        // QuizService.EnsureEnrolledStudentAsync also uses while being a deliberately different
        // rule: it refuses the teacher rather than admitting them. Keying on the message flagged it
        // on the first run, which would have meant either a false alarm forever or an exemption
        // hiding a real duplicate behind a renamed string.
        var services = Directory.EnumerateFiles(ServicesFolder(), "*.cs").ToList();
        Assert.True(services.Count >= 15, $"Only found {services.Count} service files.");

        var reimplemented = services
            .Where(path => MemberRule.IsMatch(File.ReadAllText(path)))
            .Select(Path.GetFileName)
            .Where(name => name != "ClassroomAccess.cs")
            .ToList();

        Assert.True(
            reimplemented.Count == 0,
            "These spell out the membership rule themselves instead of calling ClassroomAccess, so "
            + "the next change to it will reach some callers and not others: "
            + string.Join(", ", reimplemented));
    }

    /// <summary>
    /// "This user is the classroom's teacher, or is enrolled in it" — the disjunction that was
    /// written out five times. <c>\s</c> spans newlines, so the two-line formatting all five used
    /// matches as readily as a one-line rewrite would.
    /// </summary>
    private static readonly Regex MemberRule = new(
        @"TeacherId\s*==\s*\w+\s*\|\|\s*await\s+[\w.]*IsEnrolledAsync",
        RegexOptions.Compiled);

    [Fact]
    public void The_duplicate_detector_recognises_the_rule_it_looks_for()
    {
        // Without this, a regex that matched nothing would report "no duplicates" forever — the
        // comfortable failure, since the rule's whole output is an empty list.
        Assert.Matches(MemberRule, File.ReadAllText(Path.Combine(ServicesFolder(), "ClassroomAccess.cs")));
        Assert.DoesNotMatch(MemberRule, "if (classroom.TeacherId == userId) { throw new Exception(); }");
    }

    private static string ServicesFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, "src", "ClassroomService.Application")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "ClassroomService.Application", "Services");
    }

    // --- the world ------------------------------------------------------------------------------

    private static CreateSessionRequest NewSession() => new()
    {
        Title = "Lecture 5",
        Description = "Diffraction",
        ScheduledAtUtc = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
        ParticipationMode = StudentParticipationMode.AudioAndVideo,
        RecordingEnabled = true,
    };

    /// <summary>
    /// One classroom with one teacher, one file and one scheduled session, plus the three people
    /// who are not its teacher. Every case below differs only in who is asking.
    /// </summary>
    private sealed class World
    {
        public static readonly Guid Teacher = Guid.NewGuid();
        public static readonly Guid OtherTeacher = Guid.NewGuid();
        public static readonly Guid Student = Guid.NewGuid();
        public static readonly Guid Outsider = Guid.NewGuid();

        public readonly Guid ClassroomId = Guid.NewGuid();
        public readonly Guid ScheduledId = Guid.NewGuid();

        /// <summary>A real classroom belonging to <see cref="OtherTeacher"/>, so a refusal is never
        /// an accident of the caller owning nothing at all — and so the wrong-classroom case can be
        /// asked with two ids that both exist.</summary>
        public readonly Guid OtherClassroomId = Guid.NewGuid();

        public readonly FakeClassroomRepository Classrooms = new();
        public readonly FakeMembershipRepository Memberships = new();
        public readonly FakeSessionRepository SessionRows;
        public readonly RecordingStreamingClient Streaming = new(endResult: true);

        public readonly Session Scheduled;
        public readonly SessionService Sessions;
        public readonly ClassroomFileService Files;
        public readonly MembershipService Members;

        public World()
        {
            Classrooms.Seed(new Classroom
            {
                Id = ClassroomId,
                Name = "Physics",
                TeacherId = Teacher,
                Files = [new ClassroomFile
                {
                    Id = Guid.NewGuid(),
                    ClassroomId = ClassroomId,
                    FileName = "week-1.pdf",
                    ContentType = "application/pdf",
                    S3Key = "classrooms/physics/week-1.pdf",
                }],
            });

            Classrooms.Seed(new Classroom { Id = OtherClassroomId, Name = "Chemistry", TeacherId = OtherTeacher });

            Scheduled = new Session
            {
                Id = ScheduledId,
                ClassroomId = ClassroomId,
                Title = "Lecture 4",
                Status = SessionStatus.Scheduled,
                ScheduledAtUtc = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc),
                CreatedAtUtc = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc),
            };
            SessionRows = new FakeSessionRepository(Scheduled);

            Sessions = new SessionService(
                SessionRows, Classrooms, Memberships, Streaming,
                termination: null!, new FakeUnitOfWork());

            Files = new ClassroomFileService(
                new FakeFileRepository(), Classrooms, Memberships, new FakeFileStorageService(),
                new RecordingKnowledgeClient(), new FakeUploadSettings(), TestMapper.Create(),
                new RecordingLogger<ClassroomFileService>());

            Members = new MembershipService(Memberships, Classrooms, TestMapper.Create());
        }

        public void Enrol(Guid studentId) => Memberships.Enroll(ClassroomId, studentId);
    }
}
