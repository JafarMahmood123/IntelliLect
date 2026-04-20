namespace StreamingService.Application.DTOs.Chat;

public record ChatMessageResponse(
    Guid Id,
    Guid UserId,
    string UserName,
    string Message,
    DateTime SentAtUtc
);