namespace ClassroomService.Application.DTOs.File;

/// <summary>
/// A classroom file's bytes plus the metadata needed to serve them as an attachment. The
/// <see cref="Content"/> stream is owned by the caller and disposed once the response is written.
/// </summary>
public sealed record FileDownloadResult(Stream Content, string FileName, string ContentType);
