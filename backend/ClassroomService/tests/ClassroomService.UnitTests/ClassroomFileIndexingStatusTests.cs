using ClassroomService.Application.Exceptions;
using ClassroomService.Application.Services;
using ClassroomService.Domain.Entities;

namespace ClassroomService.UnitTests;

public sealed class ClassroomFileIndexingStatusTests
{
    private static ClassroomFileService BuildService(
        FakeClassroomRepository classrooms,
        FakeFileRepository files,
        FakeMembershipRepository memberships,
        RecordingKnowledgeClient knowledge)
        => new(
            files,
            classrooms,
            memberships,
            new FakeFileStorageService(),
            knowledge,
            new FakeUploadSettings(),
            TestMapper.Create(),
            new RecordingLogger<ClassroomFileService>());

    private static (FakeClassroomRepository Classrooms, FakeFileRepository Files, FakeMembershipRepository Memberships, ClassroomFile File)
        Seed(Guid teacherId, Guid classroomId, Guid fileId)
    {
        var classrooms = new FakeClassroomRepository();
        classrooms.Seed(new Classroom { Id = classroomId, TeacherId = teacherId });

        var files = new FakeFileRepository();
        var file = new ClassroomFile
        {
            Id = fileId,
            ClassroomId = classroomId,
            S3Key = "classrooms/x/key.pdf",
            FileName = "lecture.pdf",
            ContentType = "application/pdf",
        };
        files.Seed(file);

        return (classrooms, files, new FakeMembershipRepository(), file);
    }

    [Fact]
    public async Task Teacher_reads_the_indexing_status_from_knowledge()
    {
        var teacherId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var (classrooms, files, memberships, _) = Seed(teacherId, classroomId, fileId);
        var knowledge = new RecordingKnowledgeClient { StatusToReturn = "Processing" };
        var service = BuildService(classrooms, files, memberships, knowledge);

        var result = await service.GetFileIndexingStatusAsync(classroomId, fileId, teacherId, CancellationToken.None);

        Assert.Equal(fileId, result.FileId);
        Assert.Equal("Processing", result.Status);
        Assert.Equal(1, knowledge.StatusCalls);
        Assert.Equal(fileId, knowledge.LastStatusFileId);
    }

    [Fact]
    public async Task Enrolled_student_may_read_the_status()
    {
        var teacherId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var (classrooms, files, memberships, _) = Seed(teacherId, classroomId, fileId);
        memberships.Enroll(classroomId, studentId);
        var knowledge = new RecordingKnowledgeClient { StatusToReturn = "Done" };
        var service = BuildService(classrooms, files, memberships, knowledge);

        var result = await service.GetFileIndexingStatusAsync(classroomId, fileId, studentId, CancellationToken.None);

        Assert.Equal("Done", result.Status);
    }

    [Fact]
    public async Task Non_member_is_forbidden_and_knowledge_is_not_called()
    {
        var teacherId = Guid.NewGuid();
        var outsiderId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var (classrooms, files, memberships, _) = Seed(teacherId, classroomId, fileId);
        var knowledge = new RecordingKnowledgeClient();
        var service = BuildService(classrooms, files, memberships, knowledge);

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => service.GetFileIndexingStatusAsync(classroomId, fileId, outsiderId, CancellationToken.None));

        // A non-member must never trigger an internal call.
        Assert.Equal(0, knowledge.StatusCalls);
    }

    [Fact]
    public async Task Unknown_file_returns_not_found()
    {
        var teacherId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var (classrooms, files, memberships, _) = Seed(teacherId, classroomId, fileId);
        var knowledge = new RecordingKnowledgeClient();
        var service = BuildService(classrooms, files, memberships, knowledge);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.GetFileIndexingStatusAsync(classroomId, Guid.NewGuid(), teacherId, CancellationToken.None));
    }

    [Fact]
    public async Task File_from_another_classroom_returns_not_found()
    {
        var teacherId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var (classrooms, files, memberships, file) = Seed(teacherId, classroomId, fileId);
        file.ClassroomId = Guid.NewGuid(); // belongs elsewhere
        var knowledge = new RecordingKnowledgeClient();
        var service = BuildService(classrooms, files, memberships, knowledge);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.GetFileIndexingStatusAsync(classroomId, fileId, teacherId, CancellationToken.None));
    }

    [Fact]
    public async Task Missing_knowledge_document_is_reported_as_pending()
    {
        var teacherId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var (classrooms, files, memberships, _) = Seed(teacherId, classroomId, fileId);
        // null simulates KnowledgeService's 404 (no document row yet).
        var knowledge = new RecordingKnowledgeClient { StatusToReturn = null };
        var service = BuildService(classrooms, files, memberships, knowledge);

        var result = await service.GetFileIndexingStatusAsync(classroomId, fileId, teacherId, CancellationToken.None);

        Assert.Equal("Pending", result.Status);
    }
}
