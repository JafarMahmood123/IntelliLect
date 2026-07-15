namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Write-side storage port for recordings (R-4): deleting the underlying S3 object. Implemented
/// over the existing S3 client. Kept behind an interface so lifecycle logic is testable with a
/// mock — no real S3.
/// </summary>
public interface IRecordingStorage
{
    /// <summary>
    /// Deletes the S3 object at <paramref name="objectKey"/>. Idempotent: a missing object is a
    /// success (S3 DELETE semantics). Throws only on a hard failure, so the caller can leave the
    /// metadata row intact and retry.
    /// </summary>
    Task DeleteObjectAsync(string objectKey, CancellationToken ct = default);
}
