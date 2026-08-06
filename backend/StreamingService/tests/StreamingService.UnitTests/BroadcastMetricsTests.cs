using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using StreamingService.Application.Abstractions;
using StreamingService.Infrastructure.Observability;
using StreamingService.Presentation.Hubs;
using StreamingService.Presentation.Services;

namespace StreamingService.UnitTests;

/// <summary>
/// The server half of the latency measurement (work-plan §9.2, budgets in <c>docs/latency.md</c>).
///
/// The end-to-end numbers in <c>tests/e2e/test_latency.py</c> are taken from the client, because
/// that is what a user experiences. This is the split that makes a missed budget actionable: how
/// much of it was the hub fanning the event out, measured inside one process where there is no
/// clock skew to argue about.
///
/// Two things are worth testing and are easy to confuse. That the interface is *called* is a
/// property of <see cref="StreamHubContext"/>; that a scraper would actually *see* a measurement
/// is a property of <see cref="BroadcastMetrics"/>, and only a <see cref="MeterListener"/> proves
/// it — the same reason <c>RecordingMetricsEmitTests</c> exists in ClassroomService.
/// </summary>
public sealed class BroadcastMetricsTests
{
    // --- the instrument actually emits ------------------------------------------------

    [Fact]
    public void The_concrete_metrics_emit_a_measurement_a_scraper_would_see()
    {
        var measurements = new List<(string Instrument, double Value, string? Event)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BroadcastMetrics.MeterName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((inst, value, tags, _) =>
        {
            string? name = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "event") name = tag.Value as string;
            }
            measurements.Add((inst.Name, value, name));
        });
        listener.Start();

        // The instrument is created in the ctor, so InstrumentPublished fires and enables it.
        using var metrics = new BroadcastMetrics();
        metrics.BroadcastCompleted("ReceiveChatMessage", 0.012);

        var measurement = Assert.Single(measurements);
        Assert.Equal(BroadcastMetrics.DurationInstrument, measurement.Instrument);
        Assert.Equal(0.012, measurement.Value, precision: 6);
        Assert.Equal("ReceiveChatMessage", measurement.Event);
    }

    [Fact]
    public void Each_event_is_a_separate_series()
    {
        // Without the tag every broadcast lands in one histogram, and a slow quiz relay would be
        // averaged into fast chat traffic until it disappeared. The budgets in docs/latency.md are
        // per hop, so the metric has to be per hop too.
        var events = new List<string?>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BroadcastMetrics.MeterName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "event") events.Add(tag.Value as string);
            }
        });
        listener.Start();

        using var metrics = new BroadcastMetrics();
        metrics.BroadcastCompleted("ReceiveChatMessage", 0.01);
        metrics.BroadcastCompleted("QuizChanged", 0.02);

        Assert.Equal(["ReceiveChatMessage", "QuizChanged"], events);
    }

    // --- the hub context times the right thing, under the right name ------------------

    /// <summary>
    /// A rule over the interface rather than eight hand-written cases: every method on
    /// <see cref="IStreamHubContext"/> is invoked by reflection and must produce exactly one
    /// measurement, named for the client method it actually sent.
    ///
    /// Listing the eight by hand would pass forever while a ninth broadcast shipped untimed —
    /// which is the failure that happens, since a new broadcast is written by copying an old one
    /// and the timing lives in a shared helper that is easy to bypass.
    /// </summary>
    [Fact]
    public async Task Every_broadcast_on_the_interface_is_timed_under_the_client_method_it_invokes()
    {
        var (context, metrics, clients) = Build();
        var methods = typeof(IStreamHubContext).GetMethods();

        Assert.True(methods.Length >= 8, $"Only found {methods.Length} broadcasts to check.");

        foreach (var method in methods)
        {
            await (Task)method.Invoke(context, method.GetParameters().Select(Sample).ToArray())!;
        }

        // The recorded name has to be the name that went on the wire. Anything else and a
        // dashboard filter that works today silently matches nothing after a rename.
        Assert.Equal(clients.Invoked, metrics.Recorded.Select(r => r.Event).ToList());
        Assert.Equal(methods.Length, metrics.Recorded.Count);
    }

    private static object Sample(System.Reflection.ParameterInfo parameter) => parameter.ParameterType switch
    {
        var t when t == typeof(Guid) => Guid.NewGuid(),
        var t when t == typeof(string) => "sample",
        var t when t == typeof(bool) => true,
        var t when t == typeof(int) => 1,
        var t => throw new NotSupportedException(
            $"A broadcast now takes a {t.Name}; give this rule a sample value for it."),
    };

    [Fact]
    public async Task Every_broadcast_goes_to_the_session_group_and_only_that_group()
    {
        var (context, _, clients) = Build();
        var sessionId = Guid.NewGuid();
        var other = Guid.NewGuid();

        await context.BroadcastChatMessageAsync(sessionId, Guid.NewGuid(), "Sara", "hello");
        await context.NotifyStreamStatusChangedAsync(other, "Ended");

        Assert.Equal([sessionId.ToString(), other.ToString()], clients.GroupNames);
        // Collapsing eight methods into one helper made a copy-paste of the wrong group name a
        // single-point failure rather than an eight-point one — but only if something checks it.
        Assert.Empty(clients.NonGroupTargets);
    }

    [Fact]
    public async Task A_broadcast_that_throws_records_nothing()
    {
        var (context, metrics, clients) = Build();
        clients.Throw = new InvalidOperationException("connection gone");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.BroadcastChatMessageAsync(Guid.NewGuid(), Guid.NewGuid(), "Sara", "hi"));

        // A failed fan-out returns fast. Recording it would pull the histogram's percentiles DOWN
        // — the one direction in which a latency metric is actively misleading, because it reports
        // health precisely when things are breaking.
        Assert.Empty(metrics.Recorded);
    }

    [Fact]
    public async Task The_timing_covers_the_fan_out_rather_than_the_call_to_start_it()
    {
        var (context, metrics, clients) = Build();
        clients.Delay = TimeSpan.FromMilliseconds(40);

        await context.BroadcastChatMessageAsync(Guid.NewGuid(), Guid.NewGuid(), "Sara", "hi");

        // Timing `Clients.Group(...)` instead of the awaited send is the obvious mistake here, and
        // it would report a fast, stable, entirely fictional number: group resolution is a
        // dictionary lookup and never blocks. 40ms of delivery must show up as roughly 40ms.
        var recorded = Assert.Single(metrics.Recorded);
        Assert.True(
            recorded.Seconds >= 0.030,
            $"Recorded {recorded.Seconds:F4}s for a 40ms fan-out — the timer is not spanning the send.");
    }

    // --- doubles ----------------------------------------------------------------------

    private static (StreamHubContext Context, RecordingBroadcastMetrics Metrics, FakeHubClients Clients) Build()
    {
        var clients = new FakeHubClients();
        var metrics = new RecordingBroadcastMetrics();
        var context = new StreamHubContext(
            new FakeHubContext(clients), metrics, NullLogger<StreamHubContext>.Instance);
        return (context, metrics, clients);
    }

    private sealed class RecordingBroadcastMetrics : IBroadcastMetrics
    {
        public List<(string Event, double Seconds)> Recorded { get; } = [];

        public void BroadcastCompleted(string eventName, double seconds)
            => Recorded.Add((eventName, seconds));
    }

    private sealed class FakeHubContext : IHubContext<StreamHub, IStreamClient>
    {
        public FakeHubContext(FakeHubClients clients) => Clients = clients;

        public IHubClients<IStreamClient> Clients { get; }
        public IGroupManager Groups => throw new NotSupportedException();
    }

    /// <summary>
    /// Records which targeting method was used. Everything except <c>Group</c> throws rather than
    /// returning a working proxy: a broadcast that reached <c>All</c> would send one room's chat
    /// to every session on the server, and a fake that quietly allowed it would let that ship.
    /// </summary>
    private sealed class FakeHubClients : IHubClients<IStreamClient>, IStreamClient
    {
        public List<string> GroupNames { get; } = [];
        public List<string> NonGroupTargets { get; } = [];
        public List<string> Invoked { get; } = [];
        public Exception? Throw { get; set; }
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public IStreamClient Group(string groupName)
        {
            GroupNames.Add(groupName);
            return this;
        }

        private IStreamClient Reject(string target)
        {
            NonGroupTargets.Add(target);
            return this;
        }

        public IStreamClient All => Reject("All");
        public IStreamClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => Reject("AllExcept");
        public IStreamClient Client(string connectionId) => Reject("Client");
        public IStreamClient Clients(IReadOnlyList<string> connectionIds) => Reject("Clients");
        public IStreamClient GroupExcept(string groupName, IReadOnlyList<string> excluded) => Reject("GroupExcept");
        public IStreamClient Groups(IReadOnlyList<string> groupNames) => Reject("Groups");
        public IStreamClient User(string userId) => Reject("User");
        public IStreamClient Users(IReadOnlyList<string> userIds) => Reject("Users");

        // --- IStreamClient: every method records its own name and honours Delay/Throw ---

        private async Task Send(string name)
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay);
            if (Throw is not null) throw Throw;
            Invoked.Add(name);
        }

        public Task ReceiveHandRaise(Guid userId, bool isRaised) => Send(nameof(ReceiveHandRaise));
        public Task UpdateParticipantCount(int count) => Send(nameof(UpdateParticipantCount));
        public Task StreamStatusChanged(string status) => Send(nameof(StreamStatusChanged));
        public Task PublishPolicyChanged(bool audio, bool video) => Send(nameof(PublishPolicyChanged));
        public Task RecordingStateChanged(string state) => Send(nameof(RecordingStateChanged));
        public Task QuizChanged(Guid quizId, string state) => Send(nameof(QuizChanged));
        public Task ReceiveChatMessage(Guid userId, string userName, string message) => Send(nameof(ReceiveChatMessage));
        public Task ReceiveReaction(Guid userId, string emoji) => Send(nameof(ReceiveReaction));
    }
}
