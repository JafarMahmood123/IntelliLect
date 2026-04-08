using AutoMapper;
using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.File;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Services;

public sealed class ClassroomFileService : IClassroomFileService
{
    private readonly IRepository<ClassroomFile> _fileRepository;
    private readonly IClassroomRepository _classroomRepository;
    private readonly IFileStorageService _storageService;
    private readonly IMapper _mapper;

    public ClassroomFileService(
        IRepository<ClassroomFile> fileRepository,
        IClassroomRepository classroomRepository,
        IFileStorageService storageService,
        IMapper mapper)
    {
        _fileRepository = fileRepository;
        _classroomRepository = classroomRepository;
        _storageService = storageService;
        _mapper = mapper;
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
    }

    public async Task<IEnumerable<ClassroomFileResponse>> GetClassroomFilesAsync(Guid classroomId, CancellationToken ct)
    {
        var classroom = await _classroomRepository.GetWithDetailsAsync(classroomId, ct);
        return _mapper.Map<IEnumerable<ClassroomFileResponse>>(classroom?.Files ?? new List<ClassroomFile>());
    }
}