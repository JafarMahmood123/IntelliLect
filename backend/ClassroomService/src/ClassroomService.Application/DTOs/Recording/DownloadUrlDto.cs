namespace ClassroomService.Application.DTOs.Recording;

/// <summary>
/// A short-lived pre-signed download URL (R-3). The client downloads the MP4 directly from S3 with
/// this URL — the bytes never pass through the backend, and the raw s3_key is never exposed.
/// </summary>
public record DownloadUrlDto(string Url, DateTime ExpiresAt);
