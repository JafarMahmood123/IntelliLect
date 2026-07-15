using ClassroomService.Application.Abstractions;

namespace ClassroomService.Infrastructure.Services;

/// <summary>Real wall-clock implementation of <see cref="IClock"/>.</summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
