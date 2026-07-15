namespace ClassroomService.Application.Abstractions;

/// <summary>A minted pre-signed download URL and the instant it expires (UTC).</summary>
public sealed record PresignedUrl(string Url, DateTime ExpiresAtUtc);

/// <summary>
/// Port for minting short-lived, GET-only pre-signed download URLs (R-3). Implemented in
/// Infrastructure over the existing S3 client. Keeping it behind an interface lets the download
/// service be tested with a mock signer — no real S3, no network.
/// </summary>
public interface IRecordingUrlSigner
{
    /// <summary>
    /// Produces a GET pre-signed URL scoped to <paramref name="objectKey"/>, valid for
    /// <paramref name="ttl"/>, with the given attachment content-disposition and content-type.
    /// </summary>
    Task<PresignedUrl> GeneratePresignedGetUrlAsync(
        string objectKey,
        TimeSpan ttl,
        string contentDisposition,
        string? contentType,
        CancellationToken ct = default);
}
