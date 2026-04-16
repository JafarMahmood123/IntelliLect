namespace StreamingService.Application.Abstractions;

public interface IMediaProvider
{
    string GenerateJoinToken(Guid sessionId, Guid userId, string roleName);
}