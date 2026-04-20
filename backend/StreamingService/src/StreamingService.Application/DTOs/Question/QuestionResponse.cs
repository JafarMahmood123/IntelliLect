namespace StreamingService.Application.DTOs.Question;

public record QuestionResponse(
    Guid Id,
    Guid UserId,
    string UserName,
    string QuestionText,
    string? AnswerText,
    bool IsAnswered,
    DateTime AskedAtUtc,
    DateTime? AnsweredAtUtc
);