using Amazon.S3;
using Amazon.S3.Model;
using ClassroomService.Application.Abstractions;
using ClassroomService.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace ClassroomService.Infrastructure.Services;

/// <summary>
/// Mints GET-only pre-signed download URLs over the existing S3 client (R-3). Presigning is a
/// local signing operation — no bytes flow through the backend. Reuses the file-storage bucket
/// (<see cref="S3Settings"/>) since recordings share it.
/// </summary>
public sealed class S3RecordingUrlSigner : IRecordingUrlSigner
{
    private readonly IAmazonS3 _s3Client;
    private readonly S3Settings _s3Settings;

    public S3RecordingUrlSigner(IAmazonS3 s3Client, IOptions<S3Settings> s3Settings)
    {
        _s3Client = s3Client;
        _s3Settings = s3Settings.Value;
    }

    public async Task<PresignedUrl> GeneratePresignedGetUrlAsync(
        string objectKey,
        TimeSpan ttl,
        string contentDisposition,
        string? contentType,
        CancellationToken ct = default)
    {
        var expiresAt = DateTime.UtcNow.Add(ttl);

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _s3Settings.BucketName,
            Key = objectKey,
            Verb = HttpVerb.GET,
            Expires = expiresAt,
            ResponseHeaderOverrides = new ResponseHeaderOverrides
            {
                ContentDisposition = contentDisposition,
            },
        };

        if (!string.IsNullOrEmpty(contentType))
        {
            request.ResponseHeaderOverrides.ContentType = contentType;
        }

        var url = await _s3Client.GetPreSignedURLAsync(request);
        return new PresignedUrl(url, expiresAt);
    }
}
