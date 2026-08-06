using System.Reflection;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StreamingService.Infrastructure;
using StreamingService.Infrastructure.Consumers;

namespace StreamingService.UnitTests;

/// <summary>
/// Every consumer must be registered with a retry policy (test-plan L-04).
///
/// This service had no consumer definition of any kind, so its single consumer got one attempt
/// and then the error queue. It is the consumer that creates the <c>LiveStream</c> row for a
/// class that has just started — and it is the only thing that does, with no second publish to
/// fall back on. A database fault lasting a second left a lecture with no stream record while the
/// teacher and the students were already in the room.
///
/// Written as a rule over the composition root rather than a check for one file, because the
/// failure is one of omission and the next consumer added here would arrive exactly as bare as
/// this one did.
/// </summary>
public sealed class ConsumerRetryPolicyTests
{
    private static readonly Assembly Infrastructure = typeof(SessionStartedConsumer).Assembly;

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
        // Without this, a reflection bug that finds nothing makes the rule above pass by vacuum.
        // Update the count deliberately when a consumer is added; that is the moment to decide
        // its retry policy.
        Assert.Equal(1, Infrastructure.GetTypes().Count(IsConsumer));
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
            })
            .Build();
}
