using MassTransit;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;
using UserManagementService.Application.Common.Messages;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Infrastructure.BackgroundJobs;

public class UserStatusChangedConsumer : IConsumer<UserStatusChangedMessage>
{
    private readonly IEmailService _emailService;
    private readonly IEmailBodyFactory _bodyFactory;

    public UserStatusChangedConsumer(IEmailService emailService, IEmailBodyFactory bodyFactory)
    {
        _emailService = emailService;
        _bodyFactory = bodyFactory;
    }

    public async Task Consume(ConsumeContext<UserStatusChangedMessage> context)
    {
        // 1. Pick the correct subject from our Constants
        string subject = context.Message.NewStatus switch
        {
            UserStatus.Pending => EmailSubjects.RegistrationReceived,
            UserStatus.Active => EmailSubjects.AccountApproved,
            UserStatus.Rejected => EmailSubjects.AccountRejected,
            UserStatus.Deactivated => EmailSubjects.AccountDeactivated,
            _ => "IntelliLect Account Update"
        };

        // 2. Generate Body
        var body = _bodyFactory.CreateStatusChangedBody(context.Message.FirstName, context.Message.NewStatus);

        // 3. Send
        await _emailService.SendHtmlEmailAsync(context.Message.Email, subject, body);
    }
}