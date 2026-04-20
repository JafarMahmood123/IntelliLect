using StreamingService.Application.Abstractions;

namespace StreamingService.Infrastructure.Configuration;

public sealed class LiveKitSettings : IStreamSettings
{
    public const string SectionName = "LiveKit";
    public string ApiKey { get; init; } = null!;
    public string ApiSecret { get; init; } = null!;
    public string Host { get; init; } = null!;

    public string LiveKitHost => Host;
}