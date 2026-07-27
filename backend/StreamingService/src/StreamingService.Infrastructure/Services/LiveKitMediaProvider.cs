using System.Text.Json;
using System.Text.Json.Serialization;
using Livekit.Server.Sdk.Dotnet;
using LiveKit.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamingService.Application.Abstractions;
using StreamingService.Infrastructure.Configuration;

namespace StreamingService.Infrastructure.Services;

/// <summary>Shape of the LiveKit participant metadata JSON. Serialized as <c>{"role":"Teacher"}</c>.</summary>
internal sealed record ParticipantMetadata([property: JsonPropertyName("role")] string Role);

public sealed class LiveKitMediaProvider : IMediaProvider
{
    private readonly LiveKitSettings _settings;
    private readonly ILogger<LiveKitMediaProvider> _logger;

    public LiveKitMediaProvider(IOptions<LiveKitSettings> settings, ILogger<LiveKitMediaProvider> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public string GenerateJoinToken(
        Guid sessionId,
        Guid userId,
        string role,
        string displayName,
        bool canPublishAudio,
        bool canPublishVideo)
    {
        bool isTeacher = role.Equals("Teacher", StringComparison.OrdinalIgnoreCase);

        _logger.LogDebug(
            "Generating LiveKit join token. SessionId: {SessionId}, UserId: {UserId}, Role: {Role}, Audio: {Audio}, Video: {Video}",
            sessionId, userId, role, canPublishAudio, canPublishVideo);

        // Carry the participant's role in the LiveKit participant metadata so the client can
        // apply role-based visibility (students see only the teacher; the teacher sees everyone),
        // and so server-side policy changes can target students specifically.
        var metadata = JsonSerializer.Serialize(new ParticipantMetadata(role));

        // Identity stays the userId (stable, used for correlation); the display name is the
        // username so tiles/placeholders show a human name instead of a GUID.
        var name = string.IsNullOrWhiteSpace(displayName) ? userId.ToString() : displayName;

        var token = new AccessToken(_settings.ApiKey, _settings.ApiSecret)
            .WithIdentity(userId.ToString())
            .WithName(name)
            .WithMetadata(metadata);

        // Per-source publish rights: the explicit source list is what actually allows/denies each
        // track. CanPublish is the master switch — false when no source is allowed, so a view-only
        // student cannot publish anything. This mirrors the runtime UpdateParticipant enforcement.
        var sources = PublishSourceMap.TokenSources(canPublishAudio, canPublishVideo, isTeacher);

        var grant = new VideoGrants
        {
            Room = sessionId.ToString(),
            RoomJoin = true,
            CanPublish = sources.Count > 0,
            CanPublishSources = sources,
            CanSubscribe = true,
            CanPublishData = true
        };

        token.WithGrants(grant);

        return token.ToJwt();
    }
}
