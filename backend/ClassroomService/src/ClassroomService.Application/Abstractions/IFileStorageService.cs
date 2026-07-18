namespace ClassroomService.Application.Abstractions;

public interface IFileStorageService
{
    Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType, CancellationToken ct = default);
    Task DeleteFileAsync(string s3Key, CancellationToken ct = default);

    /// <summary>
    /// Opens a read stream for an object so the API can stream it straight to the client. Used by
    /// the gateway-routed download endpoint — bytes flow browser &lt;- API &lt;- MinIO over the same
    /// origin the app already uses, so the browser never touches the raw MinIO port. The caller
    /// owns the returned stream and must dispose it (ASP.NET's FileStreamResult does).
    /// </summary>
    Task<Stream> OpenReadAsync(string s3Key, CancellationToken ct = default);
}