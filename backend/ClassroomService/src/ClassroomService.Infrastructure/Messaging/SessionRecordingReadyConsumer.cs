using ClassroomService.Application.Abstractions;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
using IntelliLect.Contracts.Messages;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace ClassroomService.Infrastructure.Messaging;

/// <summary>
/// Transitions a session recording when StreamingService reports the egress finished (R-1).
/// Idempotent: upserts by session id, so a Processing row from R-0 is updated in place and a
/// duplicate delivery does not create a second row or corrupt state.
/// </summary>
public sealed class SessionRecordingReadyConsumer : IConsumer<SessionRecordingReadyMessage>
{
    private readonly IRecordingRepository _recordingRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRecordingMetrics _metrics;
    private readonly ILogger<SessionRecordingReadyConsumer> _logger;

    public SessionRecordingReadyConsumer(
        IRecordingRepository recordingRepository,
        IUnitOfWork unitOfWork,
        IRecordingMetrics metrics,
        ILogger<SessionRecordingReadyConsumer> logger)
    {
        _recordingRepository = recordingRepository;
        _unitOfWork = unitOfWork;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<SessionRecordingReadyMessage> context)
    {
        var message = context.Message;

        // Correlate every log in this operation by session + egress id.
        using var scope = _logger.BeginScope("session:{SessionId} egress:{EgressId}", message.SessionId, message.EgressId);

        // Upsert by session id: reuse the R-0 Processing row if present, else create one as a
        // safety net. This is what makes duplicate webhook deliveries idempotent.
        var recording = await _recordingRepository.GetBySessionIdAsync(message.SessionId);
        var isNew = recording is null;
        if (recording is null)
        {
            recording = new SessionRecording
            {
                Id = Guid.NewGuid(),
                SessionId = message.SessionId,
                CreatedAtUtc = DateTime.UtcNow,
            };
        }

        recording.ClassroomId = message.ClassroomId;
        recording.EgressId = message.EgressId;
        recording.SizeBytes = message.SizeBytes;
        recording.DurationSeconds = (int)message.Duration.TotalSeconds;
        recording.ContentType = message.ContentType;

        if (message.Succeeded)
        {
            recording.Status = RecordingStatus.Available;
            recording.S3Key = message.S3Key;
            recording.AvailableAtUtc = DateTime.UtcNow;
            recording.Error = null;
        }
        else
        {
            recording.Status = RecordingStatus.Failed;
            recording.Error = message.Error;
        }

        if (isNew)
        {
            await _recordingRepository.AddAsync(recording);
        }

        await _unitOfWork.SaveChangesAsync();

        if (message.Succeeded)
        {
            // Time from the egress-complete webhook (message publish) to Available.
            var egressToAvailable = context.SentTime is { } sent
                ? Math.Max(0, (DateTime.UtcNow - sent).TotalSeconds)
                : 0;
            _metrics.RecordingCompleted(message.SizeBytes, egressToAvailable);
        }
        else
        {
            _metrics.RecordingFailed();
        }

        _logger.LogInformation(
            "Session recording for session {SessionId} (egress {EgressId}) set to {Status}.",
            message.SessionId, message.EgressId, recording.Status);
    }
}
