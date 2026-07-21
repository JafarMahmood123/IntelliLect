using MassTransit;
using UserManagementService.Application.Abstractions;

namespace UserManagementService.Infrastructure.Messaging;

/// <summary>
/// Publishes directly through the MassTransit bus (<see cref="IBus"/>), bypassing the EF Core
/// transactional outbox that wraps the scoped <c>IPublishEndpoint</c>. Used for notifications that
/// are not tied to a local database transaction (see <see cref="INotificationBus"/>).
/// </summary>
public sealed class DirectNotificationBus : INotificationBus
{
    private readonly IBus _bus;

    public DirectNotificationBus(IBus bus)
    {
        _bus = bus;
    }

    public Task PublishAsync<T>(T message, CancellationToken ct = default)
        where T : class
        => _bus.Publish(message, ct);
}
