namespace StreamingService.Application.Abstractions;

public interface IInteractionService
{
    Task SendChatMessageAsync(Guid sessionId, Guid userId, string userName, string message, CancellationToken ct = default);
    Task SendReactionAsync(Guid sessionId, Guid userId, string emoji, CancellationToken ct = default);
    Task AskQuestionAsync(Guid sessionId, Guid userId, string userName, string questionText, CancellationToken ct = default);
    Task AnswerQuestionAsync(Guid questionId, Guid teacherId, string answerText, CancellationToken ct = default);
    Task ToggleHandRaiseAsync(Guid sessionId, Guid userId, bool isRaised, CancellationToken ct = default);
}