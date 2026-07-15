namespace ClassroomService.Application.Abstractions;

/// <summary>Abstracts the current time so time-based logic (reconcile/retention) is testable.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}
