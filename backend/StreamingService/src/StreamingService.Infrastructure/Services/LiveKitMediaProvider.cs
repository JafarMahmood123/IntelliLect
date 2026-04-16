using Livekit.Server.Sdk.Dotnet;
using LiveKit.Authentication;
using Microsoft.Extensions.Options;
using StreamingService.Application.Abstractions;
using StreamingService.Infrastructure.Configuration;
namespace StreamingService.Infrastructure.Services;

public sealed class LiveKitMediaProvider : IMediaProvider
{
    private readonly LiveKitSettings _settings;

    public LiveKitMediaProvider(IOptions<LiveKitSettings> settings)
    {
        _settings = settings.Value;
    }

    public string GenerateJoinToken(Guid sessionId, Guid userId, string roleName)
    {
        var token = new AccessToken(_settings.ApiKey, _settings.ApiSecret)
            .WithIdentity(userId.ToString())
            .WithName(userId.ToString());

        bool isTeacher = roleName.Equals("Teacher", StringComparison.OrdinalIgnoreCase);

        var grant = new VideoGrants
        {
            Room = sessionId.ToString(),
            RoomJoin = true,
            CanPublish = isTeacher,
            CanSubscribe = true,
            CanPublishData = true
        };

        token.WithGrants(grant);

        return token.ToJwt();
    }
}