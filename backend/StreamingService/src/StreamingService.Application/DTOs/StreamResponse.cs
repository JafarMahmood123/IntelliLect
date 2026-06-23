namespace StreamingService.Application.DTOs;

public record StreamResponse(
    Guid Id,
    Guid SessionId,
    string Status,
    int ParticipantCount,
    DateTime? StartedAtUtc,
    string JoinToken,
    string LiveKitHost,
    int ParticipationMode);