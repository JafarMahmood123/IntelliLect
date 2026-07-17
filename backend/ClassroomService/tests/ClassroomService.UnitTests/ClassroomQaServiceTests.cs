using ClassroomService.Application.Exceptions;
using ClassroomService.Application.Models;
using ClassroomService.Application.Services;
using ClassroomService.Domain.Entities;

namespace ClassroomService.UnitTests;

public sealed class ClassroomQaServiceTests
{
    private static ClassroomQaService BuildService(
        FakeClassroomRepository classrooms,
        FakeMembershipRepository memberships,
        RecordingKnowledgeClient knowledge)
        => new(classrooms, memberships, knowledge, new RecordingLogger<ClassroomQaService>());

    private static (FakeClassroomRepository Classrooms, FakeMembershipRepository Memberships)
        SeedClassroom(Guid teacherId, Guid classroomId)
    {
        var classrooms = new FakeClassroomRepository();
        classrooms.Seed(new Classroom { Id = classroomId, TeacherId = teacherId });
        return (classrooms, new FakeMembershipRepository());
    }

    [Fact]
    public async Task Member_gets_a_grounded_answer_with_citations()
    {
        var teacherId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        var (classrooms, memberships) = SeedClassroom(teacherId, classroomId);
        var knowledge = new RecordingKnowledgeClient();
        var service = BuildService(classrooms, memberships, knowledge);

        var result = await service.AnswerAsync(classroomId, teacherId, "What is the mitochondria?", CancellationToken.None);

        Assert.True(result.HasAnswer);
        Assert.NotEmpty(result.Answer);
        var source = Assert.Single(result.Sources);
        Assert.Equal(1, source.Citation);
        Assert.Equal(4, source.Page);
    }

    [Fact]
    public async Task Enrolled_student_may_ask()
    {
        var teacherId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        var (classrooms, memberships) = SeedClassroom(teacherId, classroomId);
        memberships.Enroll(classroomId, studentId);
        var service = BuildService(classrooms, memberships, new RecordingKnowledgeClient());

        var result = await service.AnswerAsync(classroomId, studentId, "Explain photosynthesis", CancellationToken.None);

        Assert.True(result.HasAnswer);
    }

    [Fact]
    public async Task Non_member_is_forbidden_and_knowledge_is_not_called()
    {
        var teacherId = Guid.NewGuid();
        var outsiderId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        var (classrooms, memberships) = SeedClassroom(teacherId, classroomId);
        var knowledge = new RecordingKnowledgeClient();
        var service = BuildService(classrooms, memberships, knowledge);

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => service.AnswerAsync(classroomId, outsiderId, "secret question", CancellationToken.None));

        Assert.Equal(0, knowledge.AnswerCalls);
    }

    [Fact]
    public async Task Retrieval_scope_is_the_path_classroom_id_from_verified_membership()
    {
        var teacherId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        var (classrooms, memberships) = SeedClassroom(teacherId, classroomId);
        var knowledge = new RecordingKnowledgeClient();
        var service = BuildService(classrooms, memberships, knowledge);

        await service.AnswerAsync(classroomId, teacherId, "Scope check", CancellationToken.None);

        // The scope passed to KnowledgeService is the route classroom id — there is no way for a
        // client to override it (the request body carries only the question).
        Assert.Equal(1, knowledge.AnswerCalls);
        Assert.Equal(classroomId, knowledge.LastAnswerClassroomId);
        Assert.Equal("Scope check", knowledge.LastAnswerQuestion);
    }

    [Fact]
    public async Task No_relevant_material_returns_hasAnswer_false_without_sources()
    {
        var teacherId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        var (classrooms, memberships) = SeedClassroom(teacherId, classroomId);
        var knowledge = new RecordingKnowledgeClient
        {
            AnswerToReturn = new KnowledgeAnswerResult(
                "I couldn't find relevant material for that question.",
                new List<KnowledgeAnswerSource>()),
        };
        var service = BuildService(classrooms, memberships, knowledge);

        var result = await service.AnswerAsync(classroomId, teacherId, "Unrelated question", CancellationToken.None);

        Assert.False(result.HasAnswer);
        Assert.Empty(result.Sources);
        Assert.NotEmpty(result.Answer); // a clear message, not fabricated content
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_question_is_unprocessable_and_does_not_call_knowledge(string question)
    {
        var teacherId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        var (classrooms, memberships) = SeedClassroom(teacherId, classroomId);
        var knowledge = new RecordingKnowledgeClient();
        var service = BuildService(classrooms, memberships, knowledge);

        await Assert.ThrowsAsync<ValidationException>(
            () => service.AnswerAsync(classroomId, teacherId, question, CancellationToken.None));

        Assert.Equal(0, knowledge.AnswerCalls);
    }

    [Fact]
    public async Task Unknown_classroom_returns_not_found()
    {
        var teacherId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        var (classrooms, memberships) = SeedClassroom(teacherId, classroomId);
        var service = BuildService(classrooms, memberships, new RecordingKnowledgeClient());

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.AnswerAsync(Guid.NewGuid(), teacherId, "hi", CancellationToken.None));
    }
}
