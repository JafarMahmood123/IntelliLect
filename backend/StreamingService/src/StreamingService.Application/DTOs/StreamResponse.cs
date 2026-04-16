namespace StreamingService.Application.DTOs;

public record StreamResponse(
    Guid Id,
    Guid SessionId,
    string StreamKey,
    string Status,
    int ParticipantCount,
    DateTime? StartedAtUtc);