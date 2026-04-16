using MassTransit;
using StreamingService.Domain.Entities;
using StreamingService.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using StreamingService.Application.Messages;

namespace StreamingService.Infrastructure.Consumers;

public sealed class SessionStartedConsumer : IConsumer<SessionStartedMessage>
{
    private readonly StreamingDbContext _context;
    private readonly ILogger<SessionStartedConsumer> _logger;

    public SessionStartedConsumer(StreamingDbContext context, ILogger<SessionStartedConsumer> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<SessionStartedMessage> context)
    {
        _logger.LogInformation("Processing SessionStarted for Session: {SessionId}", context.Message.SessionId);

        var existingStream = await _context.Streams
            .AnyAsync(s => s.SessionId == context.Message.SessionId);

        if (existingStream)
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

        _context.Streams.Add(stream);
        await _context.SaveChangesAsync();

        _logger.LogInformation("LiveStream created for Session: {SessionId} with Key: {StreamKey}",
            stream.SessionId, stream.StreamKey);
    }
}