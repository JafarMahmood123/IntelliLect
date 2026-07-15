using Microsoft.AspNetCore.Mvc;
using StreamingService.Domain.Entities;
using StreamingService.Domain.Enums;
using StreamingService.Presentation.Controllers;

namespace StreamingService.UnitTests;

public sealed class InternalStreamsControllerTests
{
    private static InternalStreamsController CreateController(
        FakeStreamRepository repo, RecordingLiveAssistantClient assistant, RecordingLogger<InternalStreamsController> logger)
        => new(repo, assistant, logger);

    private static InitializeStreamRequest StartRequest(Guid sessionId, Guid classroomId, Guid teacherId)
        => new(sessionId, classroomId, teacherId, default);

    [Fact]
    public async Task InitializeStream_creates_stream_and_notifies_assistant_with_room_and_teacher()
    {
        var repo = new FakeStreamRepository();
        var assistant = new RecordingLiveAssistantClient();
        var controller = CreateController(repo, assistant, new RecordingLogger<InternalStreamsController>());

        var sessionId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var result = await controller.InitializeStream(StartRequest(sessionId, Guid.NewGuid(), teacherId), default);

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(1, assistant.StartCalls);
        // Room = sessionId, teacher identity = teacherId (LiveKitMediaProvider conventions).
        Assert.Equal(sessionId.ToString(), assistant.LastRoomName);
        Assert.Equal(teacherId.ToString(), assistant.LastTeacherIdentity);
        Assert.NotNull(repo.Find(sessionId));
    }

    [Fact]
    public async Task InitializeStream_still_succeeds_when_assistant_is_unreachable()
    {
        var repo = new FakeStreamRepository();
        var assistant = new RecordingLiveAssistantClient(throwOnCall: true);
        var logger = new RecordingLogger<InternalStreamsController>();
        var controller = CreateController(repo, assistant, logger);

        var sessionId = Guid.NewGuid();
        var result = await controller.InitializeStream(StartRequest(sessionId, Guid.NewGuid(), Guid.NewGuid()), default);

        // Stream creation succeeds; the assistant failure is swallowed + logged as a warning.
        Assert.IsType<CreatedAtActionResult>(result);
        Assert.NotNull(repo.Find(sessionId));
        Assert.Equal(1, logger.WarningCount);
    }

    [Fact]
    public async Task EndStream_marks_ended_and_notifies_assistant()
    {
        var sessionId = Guid.NewGuid();
        var stream = new LiveStream
        {
            Id = Guid.NewGuid(), SessionId = sessionId, ClassroomId = Guid.NewGuid(),
            TeacherId = Guid.NewGuid(), Status = StreamStatus.Live, StreamKey = "k",
        };
        var repo = new FakeStreamRepository(stream);
        var assistant = new RecordingLiveAssistantClient();
        var controller = CreateController(repo, assistant, new RecordingLogger<InternalStreamsController>());

        var result = await controller.EndStream(sessionId, default);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(StreamStatus.Ended, stream.Status);
        Assert.NotNull(stream.EndedAtUtc);
        Assert.Equal(1, assistant.EndCalls);
        Assert.Equal(sessionId, assistant.LastSessionId);
    }

    [Fact]
    public async Task EndStream_still_succeeds_when_assistant_is_unreachable()
    {
        var sessionId = Guid.NewGuid();
        var stream = new LiveStream
        {
            Id = Guid.NewGuid(), SessionId = sessionId, ClassroomId = Guid.NewGuid(),
            TeacherId = Guid.NewGuid(), Status = StreamStatus.Live, StreamKey = "k",
        };
        var repo = new FakeStreamRepository(stream);
        var assistant = new RecordingLiveAssistantClient(throwOnCall: true);
        var logger = new RecordingLogger<InternalStreamsController>();
        var controller = CreateController(repo, assistant, logger);

        var result = await controller.EndStream(sessionId, default);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(StreamStatus.Ended, stream.Status);
        Assert.Equal(1, logger.WarningCount);
    }

    [Fact]
    public async Task EndStream_returns_not_found_for_unknown_session()
    {
        var repo = new FakeStreamRepository();
        var controller = CreateController(repo, new RecordingLiveAssistantClient(), new RecordingLogger<InternalStreamsController>());

        var result = await controller.EndStream(Guid.NewGuid(), default);

        Assert.IsType<NotFoundResult>(result);
    }
}
