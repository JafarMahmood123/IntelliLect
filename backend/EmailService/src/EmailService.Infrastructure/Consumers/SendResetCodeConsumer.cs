using EmailService.Application.Abstractions;
using IntelliLect.Contracts.Messages;
using MassTransit;

namespace EmailService.Infrastructure.Consumers;

public sealed class SendResetCodeConsumer : IConsumer<SendResetCodeMessage>
{
    private readonly IEmailSender _emailSender;

    public SendResetCodeConsumer(IEmailSender emailSender)
    {
        _emailSender = emailSender;
    }

    public async Task Consume(ConsumeContext<SendResetCodeMessage> context)
    {
        await _emailSender.SendResetCodeAsync(context.Message.Email, context.Message.Code);
    }
}
