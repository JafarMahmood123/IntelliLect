namespace UserManagementService.Application.Abstractions;

/// <summary>
/// Publishes a best-effort notification immediately, independent of the transactional outbox.
/// Unlike <see cref="IEventBus"/> (which stages messages and only flushes them on a DbContext
/// SaveChanges), this is for side-effects that follow a change committed in another service — the
/// teacher-change path performs no local database write to flush an outbox, so the message is sent
/// directly. Delivery failures must be treated as non-fatal by the caller.
/// </summary>
public interface INotificationBus
{
    Task PublishAsync<T>(T message, CancellationToken ct = default)
        where T : class;
}
