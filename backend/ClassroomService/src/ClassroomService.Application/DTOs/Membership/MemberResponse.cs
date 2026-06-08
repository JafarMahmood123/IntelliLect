namespace ClassroomService.Application.DTOs.Membership;

public record MemberResponse(
    Guid StudentId,
    string FullName,
    DateTime JoinedAtUtc);