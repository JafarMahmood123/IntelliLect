namespace StreamingService.Application.DTOs.Reaction;

public record ReactionResponse(
    Guid Id,
    Guid UserId,
    string Emoji,
    DateTime SentAtUtc
);