using ClassroomService.Application.Exceptions;
using ClassroomService.Application.Services;
using ClassroomService.Domain.Entities;

namespace ClassroomService.UnitTests;

/// <summary>
/// Upload size and format limits (test-plan E-01, E-02, E-04, E-05, E-06, E-09, E-12).
///
/// The size rule is checked against the FILE's own length, never the request's — the multipart
/// envelope is a few hundred bytes larger, and a file of exactly the maximum has to be accepted.
/// </summary>
public sealed class ClassroomFileUploadLimitsTests
{
    private const long MaxBytes = 1024;

    private static (ClassroomFileService Service, FakeFileRepository Files, FakeFileStorageService Storage)
        Build(Classroom classroom)
    {
        var files = new FakeFileRepository();
        var classrooms = new FakeClassroomRepository();
        classrooms.Seed(classroom);
        var storage = new FakeFileStorageService();

        var service = new ClassroomFileService(
            files,
            classrooms,
            new FakeMembershipRepository(),
            storage,
            new RecordingKnowledgeClient(),
            new FakeUploadSettings { MaxFileSizeBytes = MaxBytes },
            TestMapper.Create(),
            new RecordingLogger<ClassroomFileService>());

        return (service, files, storage);
    }

    private static Classroom NewClassroom(Guid teacherId)
        => new() { Id = Guid.NewGuid(), TeacherId = teacherId };

    // --- size (E-01, E-02, E-04) ------------------------------------------------

    [Fact]
    public async Task File_of_exactly_the_maximum_is_accepted()
    {
        var teacherId = Guid.NewGuid();
        var classroom = NewClassroom(teacherId);
        var (service, files, _) = Build(classroom);

        using var stream = new MemoryStream(new byte[MaxBytes]);
        var response = await service.UploadFileAsync(
            classroom.Id, teacherId, stream, "lecture.pdf", "application/pdf", CancellationToken.None);

        Assert.Equal(MaxBytes, response.SizeBytes);
        Assert.Equal(1, files.SaveChangesCount);
    }

    [Fact]
    public async Task One_byte_over_the_maximum_is_refused()
    {
        var teacherId = Guid.NewGuid();
        var classroom = NewClassroom(teacherId);
        var (service, _, _) = Build(classroom);

        using var stream = new MemoryStream(new byte[MaxBytes + 1]);

        await Assert.ThrowsAsync<PayloadTooLargeException>(() => service.UploadFileAsync(
            classroom.Id, teacherId, stream, "lecture.pdf", "application/pdf", CancellationToken.None));
    }

    [Fact]
    public async Task Empty_file_is_refused()
    {
        var teacherId = Guid.NewGuid();
        var classroom = NewClassroom(teacherId);
        var (service, _, _) = Build(classroom);

        using var stream = new MemoryStream([]);

        await Assert.ThrowsAsync<ValidationException>(() => service.UploadFileAsync(
            classroom.Id, teacherId, stream, "lecture.pdf", "application/pdf", CancellationToken.None));
    }

    // --- format (E-05, E-06) ----------------------------------------------------

    [Fact]
    public async Task Disallowed_type_is_refused_even_when_under_the_size_limit()
    {
        var teacherId = Guid.NewGuid();
        var classroom = NewClassroom(teacherId);
        var (service, _, _) = Build(classroom);

        using var stream = new MemoryStream(new byte[16]);

        await Assert.ThrowsAsync<ValidationException>(() => service.UploadFileAsync(
            classroom.Id, teacherId, stream, "clip.mp4", "video/mp4", CancellationToken.None));
    }

    [Fact]
    public async Task Content_type_parameters_do_not_defeat_the_allow_list()
    {
        var teacherId = Guid.NewGuid();
        var classroom = NewClassroom(teacherId);
        var (service, files, _) = Build(classroom);

        using var stream = new MemoryStream(new byte[16]);
        await service.UploadFileAsync(
            classroom.Id, teacherId, stream, "notes.txt", "TEXT/PLAIN; charset=utf-8", CancellationToken.None);

        Assert.Equal(1, files.SaveChangesCount);
    }

    [Fact]
    public async Task Allowed_extension_carries_a_generic_content_type()
    {
        // Browsers routinely send an empty or generic type for Markdown. The extension is the
        // signal KnowledgeService's router would use, so it is enough here too.
        var teacherId = Guid.NewGuid();
        var classroom = NewClassroom(teacherId);
        var (service, files, _) = Build(classroom);

        using var stream = new MemoryStream(new byte[16]);
        await service.UploadFileAsync(
            classroom.Id, teacherId, stream, "notes.md", "application/octet-stream", CancellationToken.None);

        Assert.Equal(1, files.SaveChangesCount);
    }

    [Fact]
    public async Task Allowed_content_type_survives_a_missing_extension()
    {
        var teacherId = Guid.NewGuid();
        var classroom = NewClassroom(teacherId);
        var (service, files, _) = Build(classroom);

        using var stream = new MemoryStream(new byte[16]);
        await service.UploadFileAsync(
            classroom.Id, teacherId, stream, "lecture", "application/pdf", CancellationToken.None);

        Assert.Equal(1, files.SaveChangesCount);
    }

    // --- a rejected upload leaves nothing behind (E-12) -------------------------

    [Fact]
    public async Task Rejected_upload_writes_no_storage_object_and_no_row()
    {
        var teacherId = Guid.NewGuid();
        var classroom = NewClassroom(teacherId);
        var (service, files, storage) = Build(classroom);

        using var stream = new MemoryStream(new byte[MaxBytes + 1]);

        await Assert.ThrowsAsync<PayloadTooLargeException>(() => service.UploadFileAsync(
            classroom.Id, teacherId, stream, "lecture.pdf", "application/pdf", CancellationToken.None));

        Assert.Equal(0, files.SaveChangesCount);
        Assert.Empty(storage.UploadedKeys);
    }

    [Fact]
    public async Task Limits_are_refused_to_a_non_teacher_before_the_file_is_examined()
    {
        // Authorization precedes validation: an outsider must not be able to probe the limits by
        // watching which oversized uploads come back 413 rather than 401.
        var classroom = NewClassroom(Guid.NewGuid());
        var (service, _, storage) = Build(classroom);

        using var stream = new MemoryStream(new byte[MaxBytes + 1]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UploadFileAsync(
            classroom.Id, Guid.NewGuid(), stream, "lecture.pdf", "application/pdf", CancellationToken.None));

        Assert.Empty(storage.UploadedKeys);
    }

    // --- the limits served to the browser (E-09) --------------------------------

    [Fact]
    public void GetUploadLimits_reports_the_configured_values()
    {
        var classroom = NewClassroom(Guid.NewGuid());
        var (service, _, _) = Build(classroom);

        var limits = service.GetUploadLimits();

        Assert.Equal(MaxBytes, limits.MaxFileSizeBytes);
        Assert.Contains("application/pdf", limits.AllowedContentTypes);
        Assert.Contains("pdf", limits.AllowedExtensions);
    }
}
