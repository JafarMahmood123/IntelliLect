namespace ClassroomService.Application.DTOs.File;

/// <summary>
/// The server-owned upload limits, delivered so the upload control cannot offer a file the server
/// will reject. The browser copy is advisory only — every value here is enforced server-side
/// regardless of what the client does with it.
/// </summary>
public sealed record UploadLimitsDto(
    long MaxFileSizeBytes,
    IReadOnlyCollection<string> AllowedContentTypes,
    IReadOnlyCollection<string> AllowedExtensions);
