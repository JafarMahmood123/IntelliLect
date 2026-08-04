using ClassroomService.Application.Abstractions;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
using IntelliLect.Contracts.Messages;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace ClassroomService.Infrastructure.Messaging;

/// <summary>
/// Transitions a session summary when RagService reports the summary pipeline finished (S-4).
/// Mirrors <see cref="SessionRecordingReadyConsumer"/>. Idempotent: upserts by session id, so a
/// Generating row is updated in place and a duplicate delivery does not create a second row.
/// </summary>
public sealed class SessionSummaryReadyConsumer : IConsumer<SessionSummaryReadyMessage>
{
    private readonly ISummaryRepository _summaryRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<SessionSummaryReadyConsumer> _logger;
    private readonly ISummaryMetrics _metrics;

    public SessionSummaryReadyConsumer(
        ISummaryRepository summaryRepository,
        IUnitOfWork unitOfWork,
        ILogger<SessionSummaryReadyConsumer> logger,
        ISummaryMetrics metrics)
    {
        _summaryRepository = summaryRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task Consume(ConsumeContext<SessionSummaryReadyMessage> context)
    {
        var message = context.Message;

        using var scope = _logger.BeginScope("session:{SessionId}", message.SessionId);

        // Upsert by session id: reuse a Generating row if present, else create one as a safety net.
        // This is what makes a duplicate delivery idempotent.
        var summary = await _summaryRepository.GetBySessionIdAsync(message.SessionId);
        var isNew = summary is null;
        if (summary is null)
        {
            summary = new SessionSummary
            {
                Id = Guid.NewGuid(),
                SessionId = message.SessionId,
                CreatedAtUtc = DateTime.UtcNow,
            };
        }

        summary.ClassroomId = message.ClassroomId;

        if (message.Succeeded)
        {
            summary.Status = SummaryStatus.Available;
            summary.MdS3Key = message.MdS3Key;
            summary.PdfS3Key = message.PdfS3Key;
            summary.AvailableAtUtc = DateTime.UtcNow;
            summary.Error = null;
        }
        else
        {
            summary.Status = SummaryStatus.Failed;
            summary.Error = message.Error;
        }

        if (isNew)
        {
            await _summaryRepository.AddAsync(summary);
        }

        await _unitOfWork.SaveChangesAsync();

        if (message.Succeeded)
        {
            _metrics.AvailableIncrement();
        }

        _logger.LogInformation(
            "Session summary for session {SessionId} set to {Status}.",
            message.SessionId, summary.Status);
    }
}
