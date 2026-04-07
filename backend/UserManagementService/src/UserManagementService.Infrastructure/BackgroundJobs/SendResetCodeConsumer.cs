using MassTransit;
using Microsoft.Extensions.Logging;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common.Messages;

namespace UserManagementService.Infrastructure.BackgroundJobs;

public class SendResetCodeConsumer : IConsumer<SendResetCodeMessage>
{
    private readonly IEmailService _emailService;
    private readonly ILogger<SendResetCodeConsumer> _logger;
    public SendResetCodeConsumer(IEmailService emailService, ILogger<SendResetCodeConsumer> logger)
    {
        _emailService = emailService;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<SendResetCodeMessage> context)
    {
        _logger.LogInformation("Attempting to send reset email to {Email}", context.Message.Email);

        try
        {
            await _emailService.SendResetCodeAsync(context.Message.Email, context.Message.Code);
            _logger.LogInformation("Email successfully sent to {Email}", context.Message.Email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {Email}. MassTransit will retry.", context.Message.Email);
            throw;
        }
    }
}