using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Session;
using ClassroomService.Application.Exceptions;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Services;

public class SessionService : ISessionService
{
    private readonly ISessionRepository _sessionRepository;
    private readonly IClassroomRepository _classroomRepository;
    private readonly IStreamingInternalClient _streamingClient;
    private readonly IUnitOfWork _unitOfWork;

    public SessionService(
        ISessionRepository sessionRepository,
        IClassroomRepository classroomRepository,
        IStreamingInternalClient streamingClient,
        IUnitOfWork unitOfWork)
    {
        _sessionRepository = sessionRepository;
        _classroomRepository = classroomRepository;
        _streamingClient = streamingClient;
        _unitOfWork = unitOfWork;
    }

    public async Task<IEnumerable<Session>> GetSessionsByClassroomAsync(Guid classroomId, CancellationToken ct = default)
    {
        return await _sessionRepository.GetByClassroomIdAsync(classroomId, ct);
    }

    public async Task<Session> CreateSessionAsync(Guid classroomId, CreateSessionRequest request, CancellationToken ct = default)
    {
        var session = new Session
        {
            Id = Guid.NewGuid(),
            ClassroomId = classroomId,
            Title = request.Title,
            Description = request.Description,
            ScheduledAtUtc = request.ScheduledAtUtc,
            CreatedAtUtc = DateTime.UtcNow,
            Status = SessionStatus.Scheduled,
            ParticipationMode = request.ParticipationMode
        };

        await _sessionRepository.AddAsync(session, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return session;
    }

    public async Task StartSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        // Start Distributed boundary via Unit of Work
        await _unitOfWork.BeginTransactionAsync(ct);

        try
        {
            var session = await _sessionRepository.GetByIdAsync(sessionId, ct);
            if (session == null) throw new KeyNotFoundException("Session not found.");

            if (session.Status != SessionStatus.Scheduled)
                throw new ConflictException("Only scheduled sessions can be started.");

            var classroom = await _classroomRepository.GetByIdAsync(session.ClassroomId, ct);
            if (classroom == null) throw new KeyNotFoundException("Associated classroom not found.");

            // Local State Change
            session.Status = SessionStatus.Live;
            session.StartedAtUtc = DateTime.UtcNow;
            await _sessionRepository.UpdateAsync(session, ct);

            // Synchronous cross-service call
            var success = await _streamingClient.CreateStreamAsync(
                session.Id,
                session.ClassroomId,
                classroom.TeacherId,
                session.ParticipationMode,
                ct);

            if (!success)
            {
                throw new Exception("Media server failed to initialize. Rolling back.");
            }

            // Commit local DB changes only if remote service succeeded
            await _unitOfWork.CommitAsync(ct);
        }
        catch
        {
            await _unitOfWork.RollbackAsync(ct);
            throw;
        }
    }
}