using EmailService.Application.Abstractions;
using IntelliLect.Contracts.Messages;
using MassTransit;

namespace EmailService.Infrastructure.Consumers;

public sealed class SendTwoFactorCodeConsumer : IConsumer<SendTwoFactorCodeMessage>
{
    private readonly IEmailSender _emailSender;

    public SendTwoFactorCodeConsumer(IEmailSender emailSender)
    {
        _emailSender = emailSender;
    }

    public async Task Consume(ConsumeContext<SendTwoFactorCodeMessage> context)
    {
        await _emailSender.SendTwoFactorCodeAsync(context.Message.Email, context.Message.Code);
    }
}
