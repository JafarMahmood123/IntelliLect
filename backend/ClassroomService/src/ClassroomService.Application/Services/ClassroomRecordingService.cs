using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs;
using ClassroomService.Application.DTOs.Recording;
using ClassroomService.Application.Exceptions;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;

namespace ClassroomService.Application.Services;

public sealed class ClassroomRecordingService : IClassroomRecordingService
{
    private readonly IRecordingRepository _recordingRepository;
    private readonly IClassroomRepository _classroomRepository;
    private readonly IMembershipRepository _membershipRepository;

    public ClassroomRecordingService(
        IRecordingRepository recordingRepository,
        IClassroomRepository classroomRepository,
        IMembershipRepository membershipRepository)
    {
        _recordingRepository = recordingRepository;
        _classroomRepository = classroomRepository;
        _membershipRepository = membershipRepository;
    }

    public async Task<PagedResult<RecordingSummaryDto>> ListRecordingsAsync(
        Guid classroomId,
        Guid requestingUserId,
        Guid? sessionId,
        RecordingStatus? status,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        await EnsureMemberAsync(classroomId, requestingUserId, ct);

        var (items, totalCount) = await _recordingRepository.ListByClassroomAsync(
            classroomId, sessionId, status, page, pageSize, ct);

        var dtos = items.Select(ToDto).ToList();
        return new PagedResult<RecordingSummaryDto>(dtos, totalCount, page, pageSize);
    }

    public async Task<RecordingSummaryDto> GetRecordingAsync(
        Guid classroomId,
        Guid recordingId,
        Guid requestingUserId,
        CancellationToken ct = default)
    {
        await EnsureMemberAsync(classroomId, requestingUserId, ct);

        var recording = await _recordingRepository.GetByIdAsync(recordingId, ct);
        // Unknown, or belongs to another classroom -> 404 (no cross-classroom leakage).
        if (recording is null || recording.ClassroomId != classroomId)
        {
            throw new KeyNotFoundException("Recording not found.");
        }

        return ToDto(recording);
    }

    /// <summary>
    /// Reuses the platform's membership rule: the classroom's teacher OR an enrolled student may
    /// view its recordings. Missing classroom -> 404; non-member -> 403.
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

    // Metadata only — never exposes S3Key/URL (that is R-3) or the internal Error detail.
    private static RecordingSummaryDto ToDto(SessionRecording r) => new(
        r.Id,
        r.SessionId,
        r.ClassroomId,
        r.Status.ToString(),
        r.DurationSeconds,
        r.SizeBytes,
        r.ContentType,
        r.CreatedAtUtc,
        r.AvailableAtUtc);
}
