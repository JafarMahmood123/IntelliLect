namespace ClassroomService.Application.DTOs.File;

public record ClassroomFileResponse(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string S3Key);