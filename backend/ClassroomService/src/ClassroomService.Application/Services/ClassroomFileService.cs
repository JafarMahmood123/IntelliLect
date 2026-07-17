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
    private readonly IMapper _mapper;
    private readonly ILogger<ClassroomFileService> _logger;

    public ClassroomFileService(
        IRepository<ClassroomFile> fileRepository,
        IClassroomRepository classroomRepository,
        IMembershipRepository membershipRepository,
        IFileStorageService storageService,
        IKnowledgeInternalClient knowledgeClient,
        IMapper mapper,
        ILogger<ClassroomFileService> logger)
    {
        _fileRepository = fileRepository;
        _classroomRepository = classroomRepository;
        _membershipRepository = membershipRepository;
        _storageService = storageService;
        _knowledgeClient = knowledgeClient;
        _mapper = mapper;
        _logger = logger;
    }

    public async Task<ClassroomFileResponse> UploadFileAsync(Guid classroomId, Guid uploaderId, Stream fileStream, string fileName, string contentType, CancellationToken ct)
    {
        var classroom = await _classroomRepository.GetByIdAsync(classroomId, ct);
        if (classroom == null || classroom.TeacherId != uploaderId)
            throw new UnauthorizedAccessException("Only the teacher can upload files.");

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