using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamingService.Application.Abstractions;
using StreamingService.Application.DTOs;
using StreamingService.Domain.Entities;

namespace StreamingService.Application.Services;

public sealed class StreamService : IStreamService
{
    private readonly IStreamRepository _streamRepository;
    private readonly IParticipantRepository _participantRepository;
    private readonly IStreamHubContext _hubContext;
    private readonly IMediaProvider _mediaProvider;
    private readonly IStreamSettings _settings;
    private readonly ILogger<StreamService> _logger;

    public StreamService(
        IStreamRepository streamRepository,
        IParticipantRepository participantRepository,
        IStreamHubContext hubContext,
        IMediaProvider mediaProvider,
        IStreamSettings settings,
        ILogger<StreamService> logger)
    {
        _streamRepository = streamRepository;
        _participantRepository = participantRepository;
        _hubContext = hubContext;
        _mediaProvider = mediaProvider;
        _settings = settings;
        _logger = logger;
    }

    public async Task<StreamResponse> GetStreamBySessionIdAsync(Guid sessionId, Guid userId, string role, CancellationToken ct)
    {
        _logger.LogInformation(
            "Fetching stream for SessionId: {SessionId}, UserId: {UserId}, Role: {Role}",
            sessionId, userId, role);

        var stream = await _streamRepository.GetBySessionIdAsync(sessionId, true, ct);
        if (stream == null)
        {
            _logger.LogWarning("Stream not found for SessionId: {SessionId}", sessionId);
            throw new KeyNotFoundException("Stream not found.");
        }

        var joinToken = _mediaProvider.GenerateJoinToken(sessionId, userId, role);

        return new StreamResponse(
            stream.Id,
            stream.SessionId,
            stream.Status.ToString(),
            stream.Participants.Count,
            stream.StartedAtUtc,
            joinToken,
            _settings.LiveKitHost);
    }

    public async Task JoinStreamAsync(Guid sessionId, Guid userId, CancellationToken ct)
    {
        var stream = await _streamRepository.GetBySessionIdAsync(sessionId, true, ct);
        if (stream == null || stream.Status != StreamStatus.Live)
        {
            _logger.LogWarning(
                "Join rejected: stream inactive or missing for SessionId: {SessionId}, UserId: {UserId}",
                sessionId, userId);
            throw new InvalidOperationException("Stream is not active.");
        }

        var isJoined = await _participantRepository.IsUserInStreamAsync(stream.Id, userId, ct);
        if (!isJoined)
        {
            await _participantRepository.AddAsync(new StreamParticipant
            {
                Id = Guid.NewGuid(),
                StreamId = stream.Id,
                UserId = userId,
                JoinedAtUtc = DateTime.UtcNow
            }, ct);

            await _participantRepository.SaveChangesAsync(ct);

            _logger.LogInformation(
                "User {UserId} joined stream for SessionId: {SessionId}",
                userId, sessionId);
        }
        else
        {
            _logger.LogInformation(
                "User {UserId} already in stream for SessionId: {SessionId}",
                userId, sessionId);
        }

        await _hubContext.NotifyParticipantCountAsync(sessionId, stream.Participants.Count + 1);
    }

    public async Task ToggleHandRaiseAsync(Guid sessionId, Guid userId, bool isRaised, CancellationToken ct)
    {
        var participant = await _participantRepository.GetBySessionAndUserAsync(sessionId, userId, ct);
        if (participant == null)
        {
            _logger.LogWarning(
                "Hand raise toggle failed: user not a participant. SessionId: {SessionId}, UserId: {UserId}",
                sessionId, userId);
            throw new InvalidOperationException("Not a participant.");
        }

        participant.IsHandRaised = isRaised;
        await _participantRepository.UpdateAsync(participant, ct);
        await _participantRepository.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Hand raise toggled for SessionId: {SessionId}, UserId: {UserId}, IsRaised: {IsRaised}",
            sessionId, userId, isRaised);

        await _hubContext.NotifyHandRaisedAsync(sessionId, userId, isRaised);
    }

    public async Task LeaveStreamAsync(Guid sessionId, Guid userId, CancellationToken ct)
    {
        var participant = await _participantRepository.GetBySessionAndUserAsync(sessionId, userId, ct);
        if (participant != null)
        {
            try
            {
                await _participantRepository.DeleteAsync(participant.Id, ct);
                await _participantRepository.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "User {UserId} left stream for SessionId: {SessionId}",
                    userId, sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Concurrency exception while leaving stream. Participant already removed.");
                _logger.LogDebug(ex, "Concurrency exception details for ParticipantId: {ParticipantId}", participant.Id);
            }
        }
        else
        {
            _logger.LogDebug(
                "Leave called but user was not a participant. SessionId: {SessionId}, UserId: {UserId}",
                sessionId, userId);
        }
    }
}