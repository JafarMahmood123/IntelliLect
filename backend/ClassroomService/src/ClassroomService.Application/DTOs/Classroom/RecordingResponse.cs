namespace ClassroomService.Application.DTOs.Classroom;

public record RecordingResponse(
    Guid Id,
    Guid SessionId,
    string SessionTitle,
    DateTime RecordedAtUtc,
    long SizeBytes,
    string Url
);