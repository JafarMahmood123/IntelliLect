namespace ClassroomService.Application.Abstractions;

public interface IFileStorageService
{
    Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType, CancellationToken ct = default);
    Task DeleteFileAsync(string s3Key, CancellationToken ct = default);
}