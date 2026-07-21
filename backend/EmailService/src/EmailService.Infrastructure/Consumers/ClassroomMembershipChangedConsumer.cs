using EmailService.Application.Abstractions;
using EmailService.Application.Common;
using IntelliLect.Contracts.Messages;
using MassTransit;

namespace EmailService.Infrastructure.Consumers;

// Notifies a student that their classroom membership changed (step 7). IsAdded selects the
// added-vs-removed wording and subject.
public sealed class ClassroomMembershipChangedConsumer : IConsumer<ClassroomMembershipChangedMessage>
{
    private readonly IEmailSender _emailSender;
    private readonly IEmailBodyFactory _emailBodyFactory;

    public ClassroomMembershipChangedConsumer(IEmailSender emailSender, IEmailBodyFactory emailBodyFactory)
    {
        _emailSender = emailSender;
        _emailBodyFactory = emailBodyFactory;
    }

    public async Task Consume(ConsumeContext<ClassroomMembershipChangedMessage> context)
    {
        var message = context.Message;

        var subject = message.IsAdded
            ? EmailSubjects.ClassroomMemberAdded
            : EmailSubjects.ClassroomMemberRemoved;

        var body = _emailBodyFactory.CreateMembershipChangedBody(
            message.FirstName, message.ClassroomName, message.IsAdded);

        await _emailSender.SendHtmlEmailAsync(message.Email, subject, body);
    }
}
