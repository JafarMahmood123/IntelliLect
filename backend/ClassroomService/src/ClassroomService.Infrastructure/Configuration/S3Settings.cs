namespace ClassroomService.Infrastructure.Configuration;

public class S3Settings
{
    public const string SectionName = "S3Settings";
    public string BucketName { get; init; } = null!;
    public string ServiceUrl { get; init; } = null!;
    public string Region { get; init; } = "us-east-1";
}