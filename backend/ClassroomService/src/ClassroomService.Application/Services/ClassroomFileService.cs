using AutoMapper;
using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.File;
using ClassroomService.Application.Exceptions;
using ClassroomService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace ClassroomService.Application.Services;

public sealed class ClassroomFileService : IClassroomFileService
{
    // KnowledgeService registers a Pending document on upload; until it has a row (or if the
    // ingest trigger failed) treat the file as still awaiting indexing.
    private const string PendingStatus = "Pending";

    private readonly IRepository<ClassroomFile> _fileRepository;
    private readonly IClassroomRepository _classroomRepository;
    private readonly IMembershipRepository _membershipRepository;
    private readonly IFileStorageService _storageService;
    private readonly IKnowledgeInternalClient _knowledgeClient;
    private readonly IUploadSettings _uploadSettings;
    private readonly IMapper _mapper;
    private readonly ILogger<ClassroomFileService> _logger;

    public ClassroomFileService(
        IRepository<ClassroomFile> fileRepository,
        IClassroomRepository classroomRepository,
        IMembershipRepository membershipRepository,
        IFileStorageService storageService,
        IKnowledgeInternalClient knowledgeClient,
        IUploadSettings uploadSettings,
        IMapper mapper,
        ILogger<ClassroomFileService> logger)
    {
        _fileRepository = fileRepository;
        _classroomRepository = classroomRepository;
        _membershipRepository = membershipRepository;
        _storageService = storageService;
        _knowledgeClient = knowledgeClient;
        _uploadSettings = uploadSettings;
        _mapper = mapper;
        _logger = logger;
    }

    public UploadLimitsDto GetUploadLimits() => new(
        _uploadSettings.MaxFileSizeBytes,
        _uploadSettings.AllowedContentTypes,
        _uploadSettings.AllowedExtensions);

    public async Task<ClassroomFileResponse> UploadFileAsync(Guid classroomId, Guid uploaderId, Stream fileStream, string fileName, string contentType, CancellationToken ct)
    {
        var classroom = await _classroomRepository.GetByIdAsync(classroomId, ct);
        if (classroom == null || classroom.TeacherId != uploaderId)
            throw new UnauthorizedAccessException("Only the teacher can upload files.");

        // Validated AFTER authorization and BEFORE anything is written: an unauthorised caller
        // should not learn the limits by probing, and a rejected file must leave no S3 object and
        // no row behind. Both checks are the authority — the resource filter ahead of them is a
        // coarse early guard on the whole request, not a per-file rule.
        ValidateSize(fileStream);
        ValidateType(fileName, contentType);

        var s3Key = $"classrooms/{classroomId}/{Guid.NewGuid()}-{fileName}";
        var url = await _storageService.UploadFileAsync(fileStream, s3Key, contentType, ct);

        var classroomFile = new ClassroomFile
        {
            Id = Guid.NewGuid(),
            FileName = fileName,
            S3Key = s3Key,
            ContentType = contentType,
            SizeBytes = fileStream.Length,
            ClassroomId = classroomId
        };

        await _fileRepository.AddAsync(classroomFile, ct);
        await _fileRepository.SaveChangesAsync(ct);

        // Trigger indexing in KnowledgeService. Non-fatal: the upload must succeed even if
        // KnowledgeService is unreachable. A failed trigger just means the file isn't indexed
        // yet; reconciliation (an outbox or a manual reindex endpoint) is future hardening.
        try
        {
            await _knowledgeClient.NotifyFileUploadedAsync(
                classroomFile.Id,
                classroomFile.ClassroomId,
                classroomFile.S3Key,
                classroomFile.FileName,
                classroomFile.ContentType,
                classroomFile.SizeBytes,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to notify KnowledgeService of uploaded file {FileId}; it will not be indexed until re-triggered.",
                classroomFile.Id);
        }

        return _mapper.Map<ClassroomFileResponse>(classroomFile);
    }

    /// <summary>
    /// The exact per-file size rule. Compares the FILE's own length, not the request's, so a file
    /// of exactly the maximum is accepted rather than being pushed over by multipart framing.
    /// </summary>
    private void ValidateSize(Stream fileStream)
    {
        if (fileStream.Length == 0)
            throw new ValidationException("The file is empty.");

        if (fileStream.Length > _uploadSettings.MaxFileSizeBytes)
            throw new PayloadTooLargeException(
                $"The file is {fileStream.Length} bytes, which exceeds the maximum upload size of " +
                $"{_uploadSettings.MaxFileSizeBytes} bytes.");
    }

    /// <summary>
    /// Accepts a file whose content type OR extension is allowed — either alone is enough.
    ///
    /// Not both, because the two signals disagree in ordinary use: browsers send an empty or
    /// generic type for Markdown, and a correct type with a missing extension is still perfectly
    /// indexable. KnowledgeService's extractor router dispatches on either signal too, so this
    /// admits exactly the set that can actually be extracted.
    /// </summary>
    private void ValidateType(string fileName, string contentType)
    {
        var normalizedType = NormalizeContentType(contentType);
        if (_uploadSettings.AllowedContentTypes.Contains(normalizedType))
            return;

        var extension = ExtensionOf(fileName);
        if (extension.Length > 0 && _uploadSettings.AllowedExtensions.Contains(extension))
            return;

        throw new ValidationException(
            $"Files of type '{contentType}' are not accepted. Allowed formats: " +
            $"{string.Join(", ", _uploadSettings.AllowedExtensions)}.");
    }

    /// <summary>Lowercase and drop any parameters (e.g. "; charset=utf-8") from a MIME type.</summary>
    private static string NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return string.Empty;

        var separatorIndex = contentType.IndexOf(';');
        var bare = separatorIndex >= 0 ? contentType[..separatorIndex] : contentType;
        return bare.Trim().ToLowerInvariant();
    }

    /// <summary>The lowercase extension without the dot, or "" if the name has none.</summary>
    private static string ExtensionOf(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Length > 1 ? extension[1..].ToLowerInvariant() : string.Empty;
    }

    public async Task DeleteFileAsync(Guid fileId, Guid uploaderId, CancellationToken ct)
    {
        var file = await _fileRepository.GetByIdAsync(fileId, ct);
        if (file == null) return;

        var classroom = await _classroomRepository.GetByIdAsync(file.ClassroomId, ct);
        if (classroom?.TeacherId != uploaderId)
            throw new UnauthorizedAccessException("Not authorized to delete this file.");

        await _storageService.DeleteFileAsync(file.S3Key, ct);
        await _fileRepository.DeleteAsync(fileId, ct);
        await _fileRepository.SaveChangesAsync(ct);

        // Non-fatal (see UploadFileAsync): the delete must succeed even if KnowledgeService
        // is unreachable. A failed trigger just means the index entry may linger until
        // re-triggered; reconciliation is future hardening.
        try
        {
            await _knowledgeClient.NotifyFileDeletedAsync(fileId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to notify KnowledgeService of deleted file {FileId}; its index entry may remain until re-triggered.",
                fileId);
        }
    }

    public async Task<IEnumerable<ClassroomFileResponse>> GetClassroomFilesAsync(Guid classroomId, CancellationToken ct)
    {
        var classroom = await _classroomRepository.GetWithDetailsAsync(classroomId, ct);
        return _mapper.Map<IEnumerable<ClassroomFileResponse>>(classroom?.Files ?? new List<ClassroomFile>());
    }

    public async Task<FileIndexingStatusResponse> GetFileIndexingStatusAsync(
        Guid classroomId, Guid fileId, Guid requestingUserId, CancellationToken ct)
    {
        // Members only: missing classroom -> 404, non-member -> 403 (before any file lookup).
        await EnsureMemberAsync(classroomId, requestingUserId, ct);

        var file = await _fileRepository.GetByIdAsync(fileId, ct);
        // Unknown, or belongs to another classroom -> 404 (no cross-classroom leakage).
        if (file is null || file.ClassroomId != classroomId)
        {
            throw new KeyNotFoundException("File not found.");
        }

        // Fetch the status from KnowledgeService server-side (secret stays inside the client).
        // A null result means KnowledgeService has no document row yet -> still Pending.
        var status = await _knowledgeClient.GetIndexingStatusAsync(fileId, ct);
        return new FileIndexingStatusResponse(fileId, status ?? PendingStatus);
    }

    public async Task<FileDownloadResult> GetFileDownloadAsync(
        Guid classroomId, Guid fileId, Guid requestingUserId, CancellationToken ct)
    {
        // Members only: missing classroom -> 404, non-member -> 403 (before any file lookup).
        await EnsureMemberAsync(classroomId, requestingUserId, ct);

        var file = await _fileRepository.GetByIdAsync(fileId, ct);
        // Unknown, or belongs to another classroom -> 404 (no cross-classroom leakage).
        if (file is null || file.ClassroomId != classroomId)
        {
            throw new KeyNotFoundException("File not found.");
        }

        // Stream straight from MinIO through the API — the browser never touches the storage port.
        var content = await _storageService.OpenReadAsync(file.S3Key, ct);

        // Audit accountability: who downloaded which file, when.
        _logger.LogInformation(
            "File {FileId} in classroom {ClassroomId} downloaded by user {UserId} at {TimestampUtc:o}.",
            fileId, classroomId, requestingUserId, DateTime.UtcNow);

        return new FileDownloadResult(content, file.FileName, file.ContentType);
    }

    /// <summary>
    /// Reuses the platform's membership rule: the classroom's teacher OR an enrolled student is a
    /// member. Missing classroom -> 404; non-member -> 403.
    /// </summary>
    private async Task EnsureMemberAsync(Guid classroomId, Guid userId, CancellationToken ct)
    {
        var classroom = await _classroomRepository.GetByIdAsync(classroomId, ct)
            ?? throw new KeyNotFoundException("Classroom not found.");

        var isMember = classroom.TeacherId == userId
            || await _membershipRepository.IsEnrolledAsync(classroomId, userId, ct);

        if (!isMember)
        {
            throw new ForbiddenAccessException("You are not a member of this classroom.");
        }
    }
}