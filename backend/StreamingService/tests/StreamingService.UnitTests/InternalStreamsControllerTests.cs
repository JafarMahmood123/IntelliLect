using Microsoft.AspNetCore.Mvc;
using StreamingService.Domain.Entities;
using StreamingService.Domain.Enums;
using StreamingService.Presentation.Controllers;

namespace StreamingService.UnitTests;

public sealed class InternalStreamsControllerTests
{
    private static InternalStreamsController CreateController(
        FakeStreamRepository repo,
        RecordingLiveAssistantClient assistant,
        RecordingLogger<InternalStreamsController> logger,
        FakeRecordingEgressService? egress = null,
        FakeRoomLifecycleService? rooms = null,
        RecordingStreamHubContext? hub = null)
        => new(
            repo,
            assistant,
            egress ?? new FakeRecordingEgressService(),
            rooms ?? new FakeRoomLifecycleService(),
            hub ?? new RecordingStreamHubContext(),
            logger);

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
    public async Task EndStream_evicts_participants_by_telling_them_and_closing_the_room()
    {
        // Ending a session must actually remove the students: they are told over the hub so they
        // leave gracefully, and the room is closed so anyone still connected is disconnected by
        // the media server regardless.
        var sessionId = Guid.NewGuid();
        var repo = new FakeStreamRepository(LiveStream(sessionId));
        var rooms = new FakeRoomLifecycleService();
        var hub = new RecordingStreamHubContext();
        var controller = CreateController(
            repo, new RecordingLiveAssistantClient(), new RecordingLogger<InternalStreamsController>(),
            rooms: rooms, hub: hub);

        var result = await controller.EndStream(sessionId, default);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal((sessionId, "Ended"), Assert.Single(hub.StatusChanges));
        // Room name == sessionId (the LiveKitMediaProvider token convention).
        Assert.Equal(1, rooms.CloseCalls);
        Assert.Equal(sessionId.ToString(), rooms.LastClosedRoom);
    }

    [Fact]
    public async Task EndStream_closes_the_room_even_when_the_broadcast_fails()
    {
        // If the hub is down the students never hear about it — the room close is what still
        // gets them out, so it must not be skipped.
        var sessionId = Guid.NewGuid();
        var repo = new FakeStreamRepository(LiveStream(sessionId));
        var rooms = new FakeRoomLifecycleService();
        var logger = new RecordingLogger<InternalStreamsController>();
        var controller = CreateController(
            repo, new RecordingLiveAssistantClient(), logger,
            rooms: rooms, hub: new RecordingStreamHubContext(throwOnStatusChange: true));

        var result = await controller.EndStream(sessionId, default);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, rooms.CloseCalls);
        Assert.Equal(1, logger.WarningCount);
    }

    [Fact]
    public async Task EndStream_still_succeeds_when_the_room_cannot_be_closed()
    {
        // The stream is already Ended in our own store; an unreachable media server must not turn
        // that into a failed request, or the caller would retry an end that already happened.
        var sessionId = Guid.NewGuid();
        var stream = LiveStream(sessionId);
        var repo = new FakeStreamRepository(stream);
        var logger = new RecordingLogger<InternalStreamsController>();
        var controller = CreateController(
            repo, new RecordingLiveAssistantClient(), logger,
            rooms: new FakeRoomLifecycleService(throwOnCall: true));

        var result = await controller.EndStream(sessionId, default);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(StreamStatus.Ended, stream.Status);
        Assert.Equal(1, logger.WarningCount);
    }

    [Fact]
    public async Task EndStream_stops_the_recording_before_closing_the_room()
    {
        // Closing the room kills the egress mid-flight; stopping it first lets LiveKit finalize
        // and upload the MP4.
        var sessionId = Guid.NewGuid();
        var stream = LiveStream(sessionId);
        stream.EgressId = "EG_end_order";
        var egress = new FakeRecordingEgressService();
        var rooms = new FakeRoomLifecycleService();
        var controller = CreateController(
            new FakeStreamRepository(stream), new RecordingLiveAssistantClient(),
            new RecordingLogger<InternalStreamsController>(), egress, rooms);

        await controller.EndStream(sessionId, default);

        Assert.Equal("EG_end_order", egress.LastStoppedEgressId);
        Assert.Equal(1, rooms.CloseCalls);
    }

    private static LiveStream LiveStream(Guid sessionId) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = sessionId,
        ClassroomId = Guid.NewGuid(),
        TeacherId = Guid.NewGuid(),
        Status = StreamStatus.Live,
        StreamKey = "k",
    };

    [Fact]
    public async Task EndStream_returns_not_found_for_unknown_session()
    {
        var repo = new FakeStreamRepository();
        var controller = CreateController(repo, new RecordingLiveAssistantClient(), new RecordingLogger<InternalStreamsController>());

        var result = await controller.EndStream(Guid.NewGuid(), default);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task InitializeStream_does_not_start_recording_because_the_room_does_not_exist_yet()
    {
        var repo = new FakeStreamRepository();
        var egress = new FakeRecordingEgressService(egressId: "EG_session42");
        var controller = CreateController(repo, new RecordingLiveAssistantClient(), new RecordingLogger<InternalStreamsController>(), egress);

        var sessionId = Guid.NewGuid();
        var result = await controller.InitializeStream(StartRequest(sessionId, Guid.NewGuid(), Guid.NewGuid()), default);

        Assert.IsType<CreatedAtActionResult>(result);
        // Recording is started later, on the room_started webhook (see LiveKitRecordingWebhookHandler),
        // because at this point the LiveKit room does not exist yet and egress would 404.
        Assert.Equal(0, egress.StartCalls);
        Assert.Null(repo.Find(sessionId)!.EgressId);
    }

    [Fact]
    public async Task InitializeStream_creates_the_stream_without_touching_the_egress_service()
    {
        // Even an egress service that throws on any call must not affect stream creation, since
        // InitializeStream never calls it.
        var repo = new FakeStreamRepository();
        var egress = new FakeRecordingEgressService(throwOnCall: true);
        var controller = CreateController(repo, new RecordingLiveAssistantClient(), new RecordingLogger<InternalStreamsController>(), egress);

        var sessionId = Guid.NewGuid();
        var result = await controller.InitializeStream(StartRequest(sessionId, Guid.NewGuid(), Guid.NewGuid()), default);

        Assert.IsType<CreatedAtActionResult>(result);
        var stream = repo.Find(sessionId);
        Assert.NotNull(stream);
        Assert.Null(stream!.EgressId);
        Assert.Equal(0, egress.StartCalls);
    }

    [Fact]
    public async Task EndStream_stops_recording_with_the_persisted_egress_id()
    {
        var sessionId = Guid.NewGuid();
        var stream = new LiveStream
        {
            Id = Guid.NewGuid(), SessionId = sessionId, ClassroomId = Guid.NewGuid(),
            TeacherId = Guid.NewGuid(), Status = StreamStatus.Live, StreamKey = "k",
            EgressId = "EG_session42",
        };
        var repo = new FakeStreamRepository(stream);
        var egress = new FakeRecordingEgressService();
        var controller = CreateController(repo, new RecordingLiveAssistantClient(), new RecordingLogger<InternalStreamsController>(), egress);

        var result = await controller.EndStream(sessionId, default);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, egress.StopCalls);
        Assert.Equal("EG_session42", egress.LastStoppedEgressId);
    }

    [Fact]
    public async Task EndStream_does_not_stop_recording_when_no_egress_id()
    {
        var sessionId = Guid.NewGuid();
        var stream = new LiveStream
        {
            Id = Guid.NewGuid(), SessionId = sessionId, ClassroomId = Guid.NewGuid(),
            TeacherId = Guid.NewGuid(), Status = StreamStatus.Live, StreamKey = "k",
        };
        var repo = new FakeStreamRepository(stream);
        var egress = new FakeRecordingEgressService();
        var controller = CreateController(repo, new RecordingLiveAssistantClient(), new RecordingLogger<InternalStreamsController>(), egress);

        var result = await controller.EndStream(sessionId, default);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(0, egress.StopCalls);
    }

    [Fact]
    public async Task EndStream_still_succeeds_when_stopping_recording_fails()
    {
        var sessionId = Guid.NewGuid();
        var stream = new LiveStream
        {
            Id = Guid.NewGuid(), SessionId = sessionId, ClassroomId = Guid.NewGuid(),
            TeacherId = Guid.NewGuid(), Status = StreamStatus.Live, StreamKey = "k",
            EgressId = "EG_session42",
        };
        var repo = new FakeStreamRepository(stream);
        var egress = new FakeRecordingEgressService(throwOnCall: true);
        var logger = new RecordingLogger<InternalStreamsController>();
        var controller = CreateController(repo, new RecordingLiveAssistantClient(), logger, egress);

        var result = await controller.EndStream(sessionId, default);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(StreamStatus.Ended, stream.Status);
        Assert.Equal(1, logger.WarningCount);
    }
}
