using EmailService.Application.Abstractions;
using EmailService.Application.Common;
using EmailService.Contracts.Messages;
using MassTransit;

namespace EmailService.Infrastructure.Consumers;

public sealed class UserStatusChangedConsumer : IConsumer<UserStatusChangedMessage>
{
    private readonly IEmailSender _emailSender;
    private readonly IEmailBodyFactory _emailBodyFactory;

    public UserStatusChangedConsumer(IEmailSender emailSender, IEmailBodyFactory emailBodyFactory)
    {
        _emailSender = emailSender;
        _emailBodyFactory = emailBodyFactory;
    }

    public async Task Consume(ConsumeContext<UserStatusChangedMessage> context)
    {
        var subject = context.Message.Status.ToLowerInvariant() switch
        {
            "pending" => EmailSubjects.RegistrationReceived,
            "active" => EmailSubjects.AccountApproved,
            "rejected" => EmailSubjects.AccountRejected,
            "deactivated" => EmailSubjects.AccountDeactivated,
            _ => "IntelliLect Account Update"
        };

        var body = _emailBodyFactory.CreateStatusChangedBody(context.Message.FirstName, context.Message.Status);
        await _emailSender.SendHtmlEmailAsync(context.Message.Email, subject, body);
    }
}
