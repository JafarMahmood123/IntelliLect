namespace ClassroomService.Application.DTOs.Classroom;

public record ClassroomResponse(
    Guid Id,
    string Name,
    string Description,
    Guid TeacherId,
    DateTime CreatedAtUtc,
    int FileCount,
    int StudentCount);