using System.Reflection;
using ClassroomService.Infrastructure;
using ClassroomService.Infrastructure.Messaging;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClassroomService.UnitTests;

/// <summary>
/// Every consumer must be registered with a retry policy (test-plan L-04).
///
/// EmailService has had this rule since §7.6, where three of its five consumers turned out to
/// have no definition. The same omission was sitting in this service and in StreamingService, and
/// nothing was looking: <c>SessionRecordingReadyConsumer</c> was registered bare on the line
/// directly above <c>SessionSummaryReadyConsumer</c>, whose registration carries a comment
/// explaining why a definition is needed.
///
/// **This checks the registration, not the existence of a type.** EmailService's version asks
/// whether a <c>ConsumerDefinition&lt;T&gt;</c> exists somewhere in the assembly, which a
/// definition that nobody wired up would satisfy — the file would be there, the retry would not.
/// Running the real <c>AddInfrastructure</c> and asking the container what it actually resolves
/// removes that gap, and cannot drift from the composition root because it IS the composition
/// root.
/// </summary>
public sealed class ConsumerRetryPolicyTests
{
    private static readonly Assembly Infrastructure = typeof(SessionRecordingReadyConsumer).Assembly;

    private static bool IsConsumer(Type type)
        => type is { IsClass: true, IsAbstract: false }
           && type.GetInterfaces().Any(i =>
               i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>));

    [Fact]
    public void Every_consumer_is_registered_with_a_definition()
    {
        var services = new ServiceCollection().AddInfrastructure(Configuration());

        var undefended = Infrastructure.GetTypes()
            .Where(IsConsumer)
            .Where(consumer => !services.Any(service =>
                service.ServiceType == typeof(IConsumerDefinition<>).MakeGenericType(consumer)))
            .Select(consumer => consumer.Name)
            .ToList();

        Assert.True(
            undefended.Count == 0,
            "These consumers are registered without a ConsumerDefinition, so they get one attempt "
            + "and then the error queue — which nobody watches: "
            + string.Join(", ", undefended));
    }

    [Fact]
    public void There_are_consumers_to_check_in_the_first_place()
    {
        // Without this, a reflection bug that finds nothing makes the rule above pass by vacuum —
        // the most comfortable kind of green. Update the count deliberately when a consumer is
        // added; that is the moment to decide its retry policy.
        Assert.Equal(2, Infrastructure.GetTypes().Count(IsConsumer));
    }

    [Fact]
    public void Every_definition_belongs_to_a_consumer_that_still_exists()
    {
        // The other direction. A definition whose consumer was renamed or deleted keeps
        // compiling, keeps looking like protection, and configures an endpoint nothing consumes.
        var orphans = Infrastructure.GetTypes()
            .Where(t => t.BaseType is { IsGenericType: true } b
                        && b.GetGenericTypeDefinition() == typeof(ConsumerDefinition<>))
            .Where(definition => !IsConsumer(definition.BaseType!.GetGenericArguments()[0]))
            .Select(definition => definition.Name)
            .ToList();

        Assert.True(orphans.Count == 0, $"Definitions with no live consumer: {string.Join(", ", orphans)}");
    }


    [Fact]
    public void Every_definition_actually_configures_a_retry()
    {
        // The registration rule above proves a definition is WIRED IN. It cannot see inside one,
        // and an empty ConfigureConsumer would satisfy it completely: the file present, the
        // registration present, the retry absent. That is not a theoretical gap — it survived as
        // a mutation until this case was written.
        //
        // So the definition is actually run, against a stand-in configurator that records what it
        // is asked to do. UseMessageRetry connects a MessageRetryConfigurationObserver, and that
        // is the signal.
        var withoutRetry = Definitions()
            .Where(definition => !ConfiguresARetry(definition))
            .Select(definition => definition.Name)
            .ToList();

        Assert.True(
            withoutRetry.Count == 0,
            $"These definitions configure no retry, so they protect nothing: {string.Join(", ", withoutRetry)}");
    }

    [Fact]
    public void The_detection_itself_still_works()
    {
        // Both directions, against definitions written here for the purpose. The check above
        // recognises a retry by an observer type that belongs to MassTransit, not to us — so an
        // upgrade that renames it would make every definition look unprotected, or worse, make
        // the detection match nothing and report success forever. This is what tells the two
        // apart: if it fails, the DETECTION is broken, not the definitions.
        Assert.True(ConfiguresARetry(typeof(ProbeWithRetry)), "a definition that does retry was not recognised");
        Assert.False(ConfiguresARetry(typeof(ProbeWithoutRetry)), "a definition that does nothing looked protected");
    }

    // --- helpers ------------------------------------------------------------------------

    private static IEnumerable<Type> Definitions()
        => Infrastructure.GetTypes().Where(t =>
            t.BaseType is { IsGenericType: true } b
            && b.GetGenericTypeDefinition() == typeof(ConsumerDefinition<>));

    /// <summary>Runs a definition and reports whether it asked for a retry.</summary>
    private static bool ConfiguresARetry(Type definitionType)
    {
        var configurator = DispatchProxy.Create<IReceiveEndpointConfigurator, ConfiguratorSpy>();
        var spy = (ConfiguratorSpy)(object)configurator;

        var consumer = definitionType.BaseType!.GetGenericArguments()[0];
        var configure = typeof(ConsumerDefinition<>).MakeGenericType(consumer)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.Name == "ConfigureConsumer" && m.GetParameters().Length == 3);

        // The consumer configurator and registration context are unused by these definitions;
        // a definition that started using them would fail here loudly rather than silently.
        configure.Invoke(Activator.CreateInstance(definitionType), [configurator, null, null]);

        return spy.Arguments.Any(type => type.Name.Contains("Retry", StringComparison.Ordinal));
    }

    /// <summary>
    /// A stand-in for the endpoint configurator that records every argument it is handed.
    ///
    /// <see cref="DispatchProxy"/> rather than a hand-written double: IReceiveEndpointConfigurator
    /// has dozens of members and implementing them all would bury the one line that matters.
    /// It is BCL, so this does not bring a mocking library into a suite that has none.
    /// </summary>
    public class ConfiguratorSpy : DispatchProxy
    {
        public List<Type> Arguments { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            foreach (var argument in args ?? [])
            {
                if (argument is not null) Arguments.Add(argument.GetType());
            }

            var returnType = targetMethod!.ReturnType;
            return returnType == typeof(void) || !returnType.IsValueType
                ? null
                : Activator.CreateInstance(returnType);
        }
    }

    private sealed class ProbeConsumer : IConsumer<ProbeMessage> { public Task Consume(ConsumeContext<ProbeMessage> context) => Task.CompletedTask; }
    public sealed record ProbeMessage(string Value);

    private sealed class ProbeWithRetry : ConsumerDefinition<ProbeConsumer>
    {
        protected override void ConfigureConsumer(
            IReceiveEndpointConfigurator endpointConfigurator,
            IConsumerConfigurator<ProbeConsumer> consumerConfigurator,
            IRegistrationContext context)
            => endpointConfigurator.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(10)));
    }

    private sealed class ProbeWithoutRetry : ConsumerDefinition<ProbeConsumer>
    {
        protected override void ConfigureConsumer(
            IReceiveEndpointConfigurator endpointConfigurator,
            IConsumerConfigurator<ProbeConsumer> consumerConfigurator,
            IRegistrationContext context)
        {
        }
    }

    private static IConfiguration Configuration()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = "Host=localhost;Database=test",
                ["Jwt:SecretKey"] = "a-signing-key-of-at-least-thirty-two-chars",
                ["RabbitMq:Username"] = "guest",
                ["RabbitMq:Password"] = "guest",
                ["S3Settings:AccessKey"] = "test-access-key",
                ["S3Settings:SecretKey"] = "test-secret-key",
            })
            .Build();
}
