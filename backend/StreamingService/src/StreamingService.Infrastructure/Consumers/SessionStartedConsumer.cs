using MassTransit;
using StreamingService.Domain.Entities;
using StreamingService.Application.Abstractions;
using Microsoft.Extensions.Logging;
using IntelliLect.Contracts.Messages;

namespace StreamingService.Infrastructure.Consumers;

public sealed class SessionStartedConsumer : IConsumer<SessionStartedMessage>
{
    private readonly IStreamRepository _streamRepository;
    private readonly ILogger<SessionStartedConsumer> _logger;

    public SessionStartedConsumer(IStreamRepository streamRepository, ILogger<SessionStartedConsumer> logger)
    {
        _streamRepository = streamRepository;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<SessionStartedMessage> context)
    {
        try
        {
            _logger.LogInformation("Processing SessionStarted for Session: {SessionId}", context.Message.SessionId);

            var exists = await _streamRepository.ExistsAsync(context.Message.SessionId);

            if (exists)
            {
                _logger.LogWarning("Stream already exists for Session: {SessionId}", context.Message.SessionId);
                return;
            }

            var stream = new LiveStream
            {
                Id = Guid.NewGuid(),
                SessionId = context.Message.SessionId,
                ClassroomId = context.Message.ClassroomId,
                Status = StreamStatus.Live,
                StartedAtUtc = DateTime.UtcNow,
                StreamKey = Guid.NewGuid().ToString("N")
            };

            await _streamRepository.AddAsync(stream);
            await _streamRepository.SaveChangesAsync();

            _logger.LogInformation(
                "LiveStream created via Repository for Session: {SessionId} with Key: {StreamKey}",
                stream.SessionId, stream.StreamKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to process SessionStarted for Session: {SessionId}",
                context.Message.SessionId);
            throw;
        }
    }
}
