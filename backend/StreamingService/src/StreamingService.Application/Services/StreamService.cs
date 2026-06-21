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
        var stream = await _streamRepository.GetBySessionIdAsync(sessionId, true, ct);
        if (stream == null) throw new KeyNotFoundException("Stream not found.");

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
            throw new InvalidOperationException("Stream is not active.");

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
        }

        // Notify all clients of the new count
        await _hubContext.NotifyParticipantCountAsync(sessionId, stream.Participants.Count + (isJoined ? 0 : 1));
    }

    public async Task LeaveStreamAsync(Guid sessionId, Guid userId, CancellationToken ct)
    {
        var stream = await _streamRepository.GetBySessionIdAsync(sessionId, true, ct);
        if (stream == null) return;

        var participant = await _participantRepository.GetBySessionAndUserAsync(sessionId, userId, ct);
        if (participant != null)
        {
            await _participantRepository.DeleteAsync(participant.Id, ct);
            await _participantRepository.SaveChangesAsync(ct);

            // Notify all clients that count decreased
            await _hubContext.NotifyParticipantCountAsync(sessionId, Math.Max(0, stream.Participants.Count - 1));
        }
    }

    public async Task ToggleHandRaiseAsync(Guid sessionId, Guid userId, bool isRaised, CancellationToken ct)
    {
        var participant = await _participantRepository.GetBySessionAndUserAsync(sessionId, userId, ct);
        if (participant == null) throw new InvalidOperationException("Not a participant.");

        participant.IsHandRaised = isRaised;
        await _participantRepository.UpdateAsync(participant, ct);
        await _participantRepository.SaveChangesAsync(ct);

        await _hubContext.NotifyHandRaisedAsync(sessionId, userId, isRaised);
    }
}