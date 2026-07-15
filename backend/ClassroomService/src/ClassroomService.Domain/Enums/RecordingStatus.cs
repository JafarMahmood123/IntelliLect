namespace ClassroomService.Domain.Enums;

/// <summary>Lifecycle of a session recording. A row starts <see cref="Processing"/> (R-0) and
/// transitions to <see cref="Available"/> or <see cref="Failed"/> when the egress finishes (R-1).</summary>
public enum RecordingStatus
{
    Processing = 0,
    Available = 1,
    Failed = 2
}
