using EmailService.Application.Abstractions;
using EmailService.Application.Common;
using IntelliLect.Contracts.Messages;
using MassTransit;

namespace EmailService.Infrastructure.Consumers;

// Notifies a teacher that a classroom's ownership changed (step 6). One message is published per
// affected teacher; IsNewTeacher selects the assigned-vs-reassigned wording and subject.
public sealed class ClassroomTeacherChangedConsumer : IConsumer<ClassroomTeacherChangedMessage>
{
    private readonly IEmailSender _emailSender;
    private readonly IEmailBodyFactory _emailBodyFactory;

    public ClassroomTeacherChangedConsumer(IEmailSender emailSender, IEmailBodyFactory emailBodyFactory)
    {
        _emailSender = emailSender;
        _emailBodyFactory = emailBodyFactory;
    }

    public async Task Consume(ConsumeContext<ClassroomTeacherChangedMessage> context)
    {
        var message = context.Message;

        var subject = message.IsNewTeacher
            ? EmailSubjects.ClassroomAssigned
            : EmailSubjects.ClassroomUnassigned;

        var body = _emailBodyFactory.CreateTeacherChangedBody(
            message.FirstName, message.ClassroomName, message.IsNewTeacher);

        await _emailSender.SendHtmlEmailAsync(message.Email, subject, body);
    }
}
