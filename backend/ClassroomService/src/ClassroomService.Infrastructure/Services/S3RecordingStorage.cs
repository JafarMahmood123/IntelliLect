using Amazon.S3;
using Amazon.S3.Model;
using ClassroomService.Application.Abstractions;
using ClassroomService.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace ClassroomService.Infrastructure.Services;

/// <summary>
/// Deletes recording objects over the existing S3 client (R-4). Reuses the file-storage bucket
/// (<see cref="S3Settings"/>) since recordings share it. S3 DeleteObject is idempotent — deleting a
/// missing key returns success — so no "not found" handling is needed.
/// </summary>
public sealed class S3RecordingStorage : IRecordingStorage
{
    private readonly IAmazonS3 _s3Client;
    private readonly S3Settings _s3Settings;

    public S3RecordingStorage(IAmazonS3 s3Client, IOptions<S3Settings> s3Settings)
    {
        _s3Client = s3Client;
        _s3Settings = s3Settings.Value;
    }

    public async Task DeleteObjectAsync(string objectKey, CancellationToken ct = default)
    {
        await _s3Client.DeleteObjectAsync(
            new DeleteObjectRequest
            {
                BucketName = _s3Settings.BucketName,
                Key = objectKey,
            },
            ct);
    }
}
