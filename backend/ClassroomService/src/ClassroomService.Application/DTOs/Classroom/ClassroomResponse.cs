namespace ClassroomService.Application.DTOs.Classroom;

public sealed record ClassroomResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public Guid TeacherId { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public int FileCount { get; init; }
    public int StudentCount { get; init; }
}