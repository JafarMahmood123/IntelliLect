namespace UserManagementService.Application.Abstractions;

public interface IEventBus
{
    Task PublishAsync<T>(T message, CancellationToken ct = default)
        where T : class;
}