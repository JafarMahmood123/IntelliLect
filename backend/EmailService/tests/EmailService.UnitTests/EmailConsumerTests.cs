using EmailService.Application.Abstractions;
using EmailService.Application.Common;
using EmailService.Infrastructure.Consumers;
using IntelliLect.Contracts.Messages;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EmailService.UnitTests;

/// <summary>
/// The five consumers, driven through MassTransit's in-memory harness — a real publish and a real
/// consume, with no broker and no SMTP.
///
/// What these protect is the routing: which address, which subject, and which body. This service
/// does nothing else, so a consumer that picks the wrong subject is the whole bug.
/// </summary>
public sealed class EmailConsumerTests
{
    /// <summary>Records what would have been sent instead of connecting to anything.</summary>
    private sealed class RecordingSender : IEmailSender
    {
        public readonly List<(string To, string Subject, string Body)> Sent = [];
        public Exception? ThrowOnSend;

        public Task SendResetCodeAsync(string email, string code)
        {
            Sent.Add((email, EmailSubjects.PasswordReset, $"reset:{code}"));
            return Fail();
        }

        public Task SendTwoFactorCodeAsync(string email, string code)
        {
            Sent.Add((email, EmailSubjects.TwoFactorCode, $"2fa:{code}"));
            return Fail();
        }

        public Task SendHtmlEmailAsync(string to, string subject, string htmlBody)
        {
            Sent.Add((to, subject, htmlBody));
            return Fail();
        }

        private Task Fail() => ThrowOnSend is null ? Task.CompletedTask : Task.FromException(ThrowOnSend);
    }

    /// <summary>Returns an identifiable stand-in so a test can tell which template was chosen.</summary>
    private sealed class StubBodyFactory : IEmailBodyFactory
    {
        public string CreatePasswordResetBody(string code) => $"body:reset:{code}";
        public string CreateTwoFactorCodeBody(string code) => $"body:2fa:{code}";
        public string CreateStatusChangedBody(string firstName, string status)
            => $"body:status:{firstName}:{status}";
        public string CreateTeacherChangedBody(string firstName, string classroomName, bool isNewTeacher)
            => $"body:teacher:{firstName}:{classroomName}:{isNewTeacher}";
        public string CreateMembershipChangedBody(string firstName, string classroomName, bool isAdded)
            => $"body:membership:{firstName}:{classroomName}:{isAdded}";
    }

    private static ServiceProvider BuildProvider(RecordingSender sender)
        => new ServiceCollection()
            .AddSingleton<IEmailSender>(sender)
            .AddSingleton<IEmailBodyFactory, StubBodyFactory>()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<SendResetCodeConsumer>();
                x.AddConsumer<SendTwoFactorCodeConsumer>();
                x.AddConsumer<UserStatusChangedConsumer>();
                x.AddConsumer<ClassroomTeacherChangedConsumer>();
                x.AddConsumer<ClassroomMembershipChangedConsumer>();
            })
            .BuildServiceProvider(true);

    /// <summary>Publishes one message and waits for the consumer to finish with it.</summary>
    private static async Task<RecordingSender> ConsumeAsync<T>(T message, RecordingSender? sender = null)
        where T : class
    {
        var recorder = sender ?? new RecordingSender();
        await using var provider = BuildProvider(recorder);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(message);
        Assert.True(await harness.Consumed.Any<T>(), $"{typeof(T).Name} was never consumed");

        return recorder;
    }

    [Fact]
    public async Task A_reset_code_goes_to_its_requester_with_the_reset_subject()
    {
        var sender = await ConsumeAsync(new SendResetCodeMessage("amina@intellilect.io", "482913"));

        var (to, subject, _) = Assert.Single(sender.Sent);
        Assert.Equal("amina@intellilect.io", to);
        Assert.Equal(EmailSubjects.PasswordReset, subject);
    }

    [Fact]
    public async Task A_two_factor_code_uses_the_login_subject_not_the_reset_one()
    {
        // Both are six digits in the same inbox; the subject is what tells them apart.
        var sender = await ConsumeAsync(new SendTwoFactorCodeMessage("amina@intellilect.io", "104857"));

        var (_, subject, _) = Assert.Single(sender.Sent);
        Assert.Equal(EmailSubjects.TwoFactorCode, subject);
    }

    [Theory]
    [InlineData("Pending", EmailSubjects.RegistrationReceived)]
    [InlineData("Active", EmailSubjects.AccountApproved)]
    [InlineData("Rejected", EmailSubjects.AccountRejected)]
    [InlineData("Deactivated", EmailSubjects.AccountDeactivated)]
    public async Task Each_status_picks_its_own_subject(string status, string expectedSubject)
    {
        // The status arrives as an enum name — capitalised. Matching only lowercase would send
        // every approved user the generic subject.
        var sender = await ConsumeAsync(
            new UserStatusChangedMessage("amina@intellilect.io", "Amina", status));

        var (to, subject, body) = Assert.Single(sender.Sent);
        Assert.Equal("amina@intellilect.io", to);
        Assert.Equal(expectedSubject, subject);
        Assert.Equal($"body:status:Amina:{status}", body);
    }

    [Fact]
    public async Task An_unrecognised_status_still_sends_something()
    {
        // Silence would be worse: a status change nobody was told about.
        var sender = await ConsumeAsync(
            new UserStatusChangedMessage("amina@intellilect.io", "Amina", "Suspended"));

        var (_, subject, _) = Assert.Single(sender.Sent);
        Assert.Equal("IntelliLect Account Update", subject);
    }

    [Theory]
    [InlineData(true, EmailSubjects.ClassroomAssigned)]
    [InlineData(false, EmailSubjects.ClassroomUnassigned)]
    public async Task Teacher_change_picks_the_subject_matching_the_direction(
        bool isNewTeacher, string expectedSubject)
    {
        var sender = await ConsumeAsync(new ClassroomTeacherChangedMessage(
            "amina@intellilect.io", "Amina", "Optics", isNewTeacher));

        var (_, subject, body) = Assert.Single(sender.Sent);
        Assert.Equal(expectedSubject, subject);
        Assert.Equal($"body:teacher:Amina:Optics:{isNewTeacher}", body);
    }

    [Theory]
    [InlineData(true, EmailSubjects.ClassroomMemberAdded)]
    [InlineData(false, EmailSubjects.ClassroomMemberRemoved)]
    public async Task Membership_change_picks_the_subject_matching_the_direction(
        bool isAdded, string expectedSubject)
    {
        // Telling a removed student they have been added is the failure this pins.
        var sender = await ConsumeAsync(new ClassroomMembershipChangedMessage(
            "bilal@intellilect.io", "Bilal", "Optics", isAdded));

        var (_, subject, body) = Assert.Single(sender.Sent);
        Assert.Equal(expectedSubject, subject);
        Assert.Equal($"body:membership:Bilal:Optics:{isAdded}", body);
    }

    [Fact]
    public async Task A_failed_send_faults_the_message_instead_of_being_swallowed()
    {
        // The single most important behaviour in this service. A consumer that caught and logged
        // an SMTP failure would report success to the broker, the message would be acknowledged,
        // and the retry policy would never run — the email is gone and nothing says so.
        var sender = new RecordingSender { ThrowOnSend = new InvalidOperationException("smtp down") };
        await using var provider = BuildProvider(sender);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new UserStatusChangedMessage("amina@intellilect.io", "Amina", "Active"));

        // A published Fault<T> is the broker being told the message was NOT handled, which is what
        // makes the retry policy and the error queue possible.
        Assert.True(await harness.Published.Any<Fault<UserStatusChangedMessage>>());
        Assert.Single(sender.Sent);  // it was attempted, not skipped
    }
}
