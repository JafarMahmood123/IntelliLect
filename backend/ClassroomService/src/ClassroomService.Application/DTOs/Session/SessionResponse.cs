using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.DTOs.Session;

public record SessionResponse(Guid Id, string Title, string Description, SessionStatus Status, DateTime ScheduledAtUtc, Guid ClassroomId);