using StreamingService.Application.DTOs;
using StreamingService.Application.DTOs.Chat;
using StreamingService.Application.DTOs.Question;

namespace StreamingService.Application.Abstractions;

public interface IInteractionService
{
    Task SendChatMessageAsync(Guid sessionId, Guid userId, string userName, string message, CancellationToken ct = default);
    Task SendReactionAsync(Guid sessionId, Guid userId, string emoji, CancellationToken ct = default);
    Task ToggleHandRaiseAsync(Guid sessionId, Guid userId, bool isRaised, CancellationToken ct = default);
    Task AskQuestionAsync(Guid sessionId, Guid userId, string userName, string questionText, CancellationToken ct = default);
    Task AnswerQuestionAsync(Guid questionId, Guid teacherId, string answerText, CancellationToken ct = default);
    Task<PagedResult<ChatMessageResponse>> GetChatHistoryPagedAsync(Guid sessionId, int page, int pageSize, CancellationToken ct = default);
    Task<PagedResult<QuestionResponse>> GetQuestionsPagedAsync(Guid sessionId, int page, int pageSize, CancellationToken ct = default);
}