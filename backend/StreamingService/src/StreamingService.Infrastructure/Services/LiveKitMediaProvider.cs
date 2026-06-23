using Livekit.Server.Sdk.Dotnet;
using LiveKit.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamingService.Application.Abstractions;
using StreamingService.Infrastructure.Configuration;

namespace StreamingService.Infrastructure.Services;

public sealed class LiveKitMediaProvider : IMediaProvider
{
    private readonly LiveKitSettings _settings;
    private readonly ILogger<LiveKitMediaProvider> _logger;

    public LiveKitMediaProvider(IOptions<LiveKitSettings> settings, ILogger<LiveKitMediaProvider> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public string GenerateJoinToken(Guid sessionId, Guid userId, bool canPublish)
    {
        _logger.LogDebug(
            "Generating LiveKit join token. SessionId: {SessionId}, UserId: {UserId}, CanPublish: {CanPublish}",
            sessionId, userId, canPublish);

        var token = new AccessToken(_settings.ApiKey, _settings.ApiSecret)
            .WithIdentity(userId.ToString())
            .WithName(userId.ToString());

        var grant = new VideoGrants
        {
            Room = sessionId.ToString(),
            RoomJoin = true,
            CanPublish = canPublish,
            CanSubscribe = true,
            CanPublishData = true
        };

        token.WithGrants(grant);

        return token.ToJwt();
    }
}
