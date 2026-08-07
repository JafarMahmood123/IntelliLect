using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Session;
using ClassroomService.Application.Exceptions;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Services;

public class SessionService : ISessionService
{
    private readonly ISessionRepository _sessionRepository;
    private readonly IClassroomRepository _classroomRepository;
    private readonly IMembershipRepository _membershipRepository;
    private readonly IStreamingInternalClient _streamingClient;
    private readonly ISessionTerminationService _termination;
    private readonly IUnitOfWork _unitOfWork;

    public SessionService(
        ISessionRepository sessionRepository,
        IClassroomRepository classroomRepository,
        IMembershipRepository membershipRepository,
        IStreamingInternalClient streamingClient,
        ISessionTerminationService termination,
        IUnitOfWork unitOfWork)
    {
        _sessionRepository = sessionRepository;
        _classroomRepository = classroomRepository;
        _membershipRepository = membershipRepository;
        _streamingClient = streamingClient;
        _termination = termination;
        _unitOfWork = unitOfWork;
    }

    /// <summary>
    /// The classroom's timetable, for its own members.
    ///
    /// This took no caller at all and returned any classroom's sessions to any authenticated user —
    /// titles, descriptions and schedule, addressable by a classroom id, which appears in every URL
    /// a student uses. Every sibling read on this surface (files, recordings, summaries, Q&amp;A) is
    /// member-gated; this one was written before them and nothing noticed it had been left out.
    /// </summary>
    public async Task<IEnumerable<Session>> GetSessionsByClassroomAsync(
        Guid classroomId, Guid requestingUserId, CancellationToken ct = default)
    {
        await ClassroomAccess.EnsureMemberAsync(
            _classroomRepository, _membershipRepository, classroomId, requestingUserId, ct);

        return await _sessionRepository.GetByClassroomIdAsync(classroomId, ct);
    }

    /// <summary>
    /// Schedules a session in a classroom the caller owns.
    ///
    /// The route carried <c>[Authorize(Roles = "Teacher")]</c> and nothing else, so any teacher in
    /// the platform could put a session on any other teacher's timetable — visible to that
    /// classroom's students, and startable, since <see cref="StartSessionAsync"/> had the same hole.
    /// A role says what kind of user someone is, never whose classroom this is.
    /// </summary>
    public async Task<Session> CreateSessionAsync(
        Guid classroomId, Guid requestingUserId, CreateSessionRequest request, CancellationToken ct = default)
    {
        await ClassroomAccess.EnsureTeacherAsync(_classroomRepository, classroomId, requestingUserId, ct);

        var session = new Session
        {
            Id = Guid.NewGuid(),
            ClassroomId = classroomId,
            Title = request.Title,
            Description = request.Description,
            ScheduledAtUtc = request.ScheduledAtUtc,
            CreatedAtUtc = DateTime.UtcNow,
            Status = SessionStatus.Scheduled,
            ParticipationMode = request.ParticipationMode,
            RecordingEnabled = request.RecordingEnabled
        };

        await _sessionRepository.AddAsync(session, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>
    /// Takes a scheduled session live: the row flips to <c>Live</c> and StreamingService opens the
    /// media room. Only the owning teacher may do it.
    ///
    /// This was the worst of the three. It took a bare <c>sessionId</c> — no classroom, no caller —
    /// while its route is <c>/api/classrooms/{classroomId}/sessions/{sessionId}/start</c>, so the
    /// classroom in the URL was decorative and never bound. Any teacher could start any session in
    /// the platform by id: the class goes live, the room opens, recording begins if the session was
    /// configured for it, and the teacher who actually owns it later gets "Only scheduled sessions
    /// can be started" with nothing saying who did it.
    ///
    /// The scoping is <see cref="EndSessionAsync"/>'s, deliberately: a session addressed under the
    /// wrong classroom is 404 rather than 403, so the route cannot be used to probe for sessions
    /// elsewhere. The ownership check follows it, so both refusals look the same from outside.
    /// </summary>
    public async Task StartSessionAsync(
        Guid classroomId, Guid sessionId, Guid requestingUserId, CancellationToken ct = default)
    {
        // Start Distributed boundary via Unit of Work
        await _unitOfWork.BeginTransactionAsync(ct);

        try
        {
            var session = await _sessionRepository.GetByIdAsync(sessionId, ct);
            if (session is null || session.ClassroomId != classroomId)
                throw new KeyNotFoundException("Session not found.");

            var classroom = await ClassroomAccess.EnsureTeacherAsync(
                _classroomRepository, classroomId, requestingUserId, ct);

            if (session.Status != SessionStatus.Scheduled)
                throw new ConflictException("Only scheduled sessions can be started.");

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
                session.RecordingEnabled,
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

    public async Task<SessionEndOutcome> EndSessionAsync(
        Guid classroomId, Guid sessionId, Guid requestingUserId, CancellationToken ct = default)
    {
        var session = await _sessionRepository.GetByIdAsync(sessionId, ct);

        // A session addressed under the wrong classroom is treated as missing rather than
        // forbidden, so the route cannot be used to probe for sessions in other classrooms.
        if (session is null || session.ClassroomId != classroomId)
        {
            throw new KeyNotFoundException("Session not found.");
        }

        var classroom = await _classroomRepository.GetByIdAsync(classroomId, ct);
        if (classroom is null)
        {
            throw new KeyNotFoundException("Associated classroom not found.");
        }

        // Only the teacher who owns the classroom may close its sessions. Another teacher holding
        // a valid Teacher role token is still refused.
        if (classroom.TeacherId != requestingUserId)
        {
            throw new ForbiddenAccessException("Only the classroom's teacher can end this session.");
        }

        return await _termination.EndAsync(sessionId, SessionEndTrigger.Teacher, "Ended by the teacher.", ct);
    }
}