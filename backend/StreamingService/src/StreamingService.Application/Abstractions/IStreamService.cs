using StreamingService.Application.DTOs;

namespace StreamingService.Application.Abstractions;

public interface IStreamService
{
    Task<StreamResponse> GetStreamBySessionIdAsync(Guid sessionId, CancellationToken ct = default);
    Task JoinStreamAsync(Guid sessionId, Guid userId, CancellationToken ct = default);
    Task LeaveStreamAsync(Guid sessionId, Guid userId, CancellationToken ct = default);
    Task ToggleHandRaiseAsync(Guid sessionId, Guid userId, bool isRaised, CancellationToken ct = default);
}