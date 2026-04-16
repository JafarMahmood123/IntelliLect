namespace ClassroomService.Application.DTOs.Session;

public record CreateSessionRequest(
    string Title,
    string Description,
    DateTime ScheduledAtUtc);