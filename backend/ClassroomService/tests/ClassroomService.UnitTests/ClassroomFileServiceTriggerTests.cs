using ClassroomService.Application.Services;
using ClassroomService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace ClassroomService.UnitTests;

public sealed class ClassroomFileServiceTriggerTests
{
    private static (ClassroomFileService Service, FakeFileRepository Files, RecordingLogger<ClassroomFileService> Logger)
        Build(RecordingKnowledgeClient knowledge, Classroom classroom, ClassroomFile? seedFile = null)
    {
        var files = new FakeFileRepository();
        if (seedFile is not null) files.Seed(seedFile);
        var classrooms = new FakeClassroomRepository();
        classrooms.Seed(classroom);
        var logger = new RecordingLogger<ClassroomFileService>();

        var service = new ClassroomFileService(
            files,
            classrooms,
            new FakeFileStorageService(),
            knowledge,
            TestMapper.Create(),
            logger);

        return (service, files, logger);
    }

    [Fact]
    public async Task Upload_notifies_knowledge_after_persisting()
    {
        var teacherId = Guid.NewGuid();
        var classroom = new Classroom { Id = Guid.NewGuid(), TeacherId = teacherId };
        var knowledge = new RecordingKnowledgeClient();
        var (service, files, logger) = Build(knowledge, classroom);

        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        var response = await service.UploadFileAsync(
            classroom.Id, teacherId, stream, "lecture.pdf", "application/pdf", CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(1, files.SaveChangesCount);          // persisted first
        Assert.Equal(1, knowledge.UploadCalls);           // then notified
        Assert.Equal(0, logger.WarningCount);             // no warning on success
    }

    [Fact]
    public async Task Upload_still_succeeds_and_warns_when_knowledge_is_down()
    {
        var teacherId = Guid.NewGuid();
        var classroom = new Classroom { Id = Guid.NewGuid(), TeacherId = teacherId };
        var knowledge = new RecordingKnowledgeClient(throwOnCall: true);
        var (service, files, logger) = Build(knowledge, classroom);

        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });

        // Primary operation must NOT throw even though the trigger failed.
        var response = await service.UploadFileAsync(
            classroom.Id, teacherId, stream, "lecture.pdf", "application/pdf", CancellationToken.None);

        Assert.NotNull(response);                 // upload succeeded
        Assert.Equal(1, files.SaveChangesCount);  // file was persisted
        Assert.Equal(1, knowledge.UploadCalls);   // notify was attempted
        Assert.Equal(1, logger.WarningCount);     // failure logged as a warning
    }

    [Fact]
    public async Task Delete_notifies_knowledge_after_removal()
    {
        var teacherId = Guid.NewGuid();
        var classroom = new Classroom { Id = Guid.NewGuid(), TeacherId = teacherId };
        var file = new ClassroomFile
        {
            Id = Guid.NewGuid(),
            ClassroomId = classroom.Id,
            S3Key = "classrooms/x/key.pdf",
            FileName = "lecture.pdf",
            ContentType = "application/pdf",
        };
        var knowledge = new RecordingKnowledgeClient();
        var (service, _, logger) = Build(knowledge, classroom, file);

        await service.DeleteFileAsync(file.Id, teacherId, CancellationToken.None);

        Assert.Equal(1, knowledge.DeleteCalls);
        Assert.Equal(file.Id, knowledge.LastFileId);
        Assert.Equal(0, logger.WarningCount);
    }

    [Fact]
    public async Task Delete_still_succeeds_and_warns_when_knowledge_is_down()
    {
        var teacherId = Guid.NewGuid();
        var classroom = new Classroom { Id = Guid.NewGuid(), TeacherId = teacherId };
        var file = new ClassroomFile
        {
            Id = Guid.NewGuid(),
            ClassroomId = classroom.Id,
            S3Key = "classrooms/x/key.pdf",
            FileName = "lecture.pdf",
            ContentType = "application/pdf",
        };
        var knowledge = new RecordingKnowledgeClient(throwOnCall: true);
        var (service, _, logger) = Build(knowledge, classroom, file);

        // Must not throw despite the failed trigger.
        await service.DeleteFileAsync(file.Id, teacherId, CancellationToken.None);

        Assert.Equal(1, knowledge.DeleteCalls);   // notify attempted
        Assert.Equal(1, logger.WarningCount);      // failure logged as a warning
    }
}
