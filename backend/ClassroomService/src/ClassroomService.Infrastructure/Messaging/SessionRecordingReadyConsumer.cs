using ClassroomService.Application.Abstractions;
using ClassroomService.Domain.Entities;
using IntelliLect.Contracts.Messages;
using MassTransit;

namespace ClassroomService.Infrastructure.Messaging;

public sealed class SessionRecordingReadyConsumer : IConsumer<SessionRecordingReadyMessage>
{
    private readonly IRecordingRepository _recordingRepository;
    private readonly IUnitOfWork _unitOfWork;

    public SessionRecordingReadyConsumer(IRecordingRepository recordingRepository, IUnitOfWork unitOfWork)
    {
        _recordingRepository = recordingRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task Consume(ConsumeContext<SessionRecordingReadyMessage> context)
    {
        var recording = new SessionRecording
        {
            Id = Guid.NewGuid(),
            SessionId = context.Message.SessionId,
            S3Key = context.Message.S3Key,
            SizeBytes = context.Message.SizeBytes,
            CreatedAtUtc = DateTime.UtcNow
        };

        await _recordingRepository.AddAsync(recording);

        await _unitOfWork.SaveChangesAsync();
    }
}