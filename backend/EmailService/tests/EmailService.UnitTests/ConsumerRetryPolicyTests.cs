using System.Reflection;
using EmailService.Infrastructure.Consumers;
using MassTransit;

namespace EmailService.UnitTests;

/// <summary>
/// Every consumer must ship with a retry policy.
///
/// Written as a rule over the assembly rather than a list of the five consumers that exist today,
/// because the failure it guards against is one of omission. Two of the five had a definition and
/// three did not — not by decision, just because nobody added one — which meant a single SMTP
/// hiccup sent an approval or enrolment email to the error queue, and nobody watches an error
/// queue for the email that told a student they were accepted.
///
/// A list would have to be remembered. A rule catches the sixth consumer on the day it is written.
/// </summary>
public sealed class ConsumerRetryPolicyTests
{
    private static readonly Assembly Infrastructure = typeof(UserStatusChangedConsumer).Assembly;

    private static bool IsConsumer(Type type)
        => type is { IsClass: true, IsAbstract: false }
           && type.GetInterfaces().Any(i =>
               i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>));

    private static Type? DefinitionFor(Type consumer)
        => Infrastructure.GetTypes().FirstOrDefault(candidate =>
            candidate.BaseType is { IsGenericType: true } baseType
            && baseType.GetGenericTypeDefinition() == typeof(ConsumerDefinition<>)
            && baseType.GetGenericArguments()[0] == consumer);

    [Fact]
    public void Every_consumer_has_a_definition_carrying_a_retry_policy()
    {
        var undefended = Infrastructure.GetTypes()
            .Where(IsConsumer)
            .Where(consumer => DefinitionFor(consumer) is null)
            .Select(consumer => consumer.Name)
            .ToList();

        Assert.True(
            undefended.Count == 0,
            $"These consumers have no ConsumerDefinition, so they get no retry and a transient "
            + $"SMTP failure loses the email outright: {string.Join(", ", undefended)}");
    }

    [Fact]
    public void There_are_consumers_to_check_in_the_first_place()
    {
        // Without this, a reflection bug that finds nothing would make the rule above pass by
        // vacuum — the most comfortable kind of green.
        Assert.Equal(5, Infrastructure.GetTypes().Count(IsConsumer));
    }
}
