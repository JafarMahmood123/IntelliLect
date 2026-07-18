using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using ClassroomService.Application.Abstractions;
using ClassroomService.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace ClassroomService.Infrastructure.Services;

public sealed class S3FileStorageService : IFileStorageService
{
    private readonly IAmazonS3 _s3Client;
    private readonly S3Settings _settings;

    public S3FileStorageService(IAmazonS3 s3Client, IOptions<S3Settings> settings)
    {
        _s3Client = s3Client;
        _settings = settings.Value;
    }

    public async Task<string> UploadFileAsync(
        Stream fileStream,
        string s3Key,
        string contentType,
        CancellationToken ct = default)
    {
        var bucketExists = await Amazon.S3.Util.AmazonS3Util.DoesS3BucketExistV2Async(_s3Client, _settings.BucketName);
        if (!bucketExists)
        {
            await _s3Client.PutBucketAsync(_settings.BucketName, ct);
        }

        var uploadRequest = new TransferUtilityUploadRequest
        {
            InputStream = fileStream,
            Key = s3Key,
            BucketName = _settings.BucketName,
            ContentType = contentType,
            PartSize = 6291456, // 6 MB
        };

        var fileTransferUtility = new TransferUtility(_s3Client);
        await fileTransferUtility.UploadAsync(uploadRequest, ct);

        return s3Key;
    }

    public async Task<Stream> OpenReadAsync(string s3Key, CancellationToken ct = default)
    {
        // ResponseStream stays connected to S3/MinIO until read; the caller (FileStreamResult)
        // disposes it, which releases the underlying HTTP response.
        var response = await _s3Client.GetObjectAsync(_settings.BucketName, s3Key, ct);
        return response.ResponseStream;
    }

    public async Task DeleteFileAsync(string s3Key, CancellationToken ct = default)
    {
        var deleteObjectRequest = new DeleteObjectRequest
        {
            BucketName = _settings.BucketName,
            Key = s3Key
        };

        await _s3Client.DeleteObjectAsync(deleteObjectRequest, ct);
    }
}