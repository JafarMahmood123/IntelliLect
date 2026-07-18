namespace ClassroomService.Application.DTOs;

/// <summary>
/// Where an artifact (recording, summary, file) lives in storage plus how to present it, resolved
/// AFTER authorization/status checks. Used internally by the streaming download endpoints — the
/// controller opens the object and streams it through the API/gateway. Never returned to clients.
/// </summary>
public sealed record FileDownloadTarget(string S3Key, string FileName, string ContentType);
