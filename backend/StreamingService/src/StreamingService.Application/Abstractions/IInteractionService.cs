using StreamingService.Application.DTOs;
using StreamingService.Application.DTOs.Chat;
using StreamingService.Application.DTOs.Question;

namespace StreamingService.Application.Abstractions;

/// <summary>
/// What happens inside a live lecture: chat, reactions, questions, hands.
///
/// Every method here is scoped to a session and every one of them needs to know who is asking. The
/// two read methods took no caller at all, and the three write methods took one and never consulted
/// it — see <c>StreamInteractionAuthorizationTests</c>.
/// </summary>
public interface IInteractionService
{
    Task SendChatMessageAsync(Guid sessionId, Guid userId, string userName, string message, CancellationToken ct = default);
    Task SendReactionAsync(Guid sessionId, Guid userId, string emoji, CancellationToken ct = default);
    Task ToggleHandRaiseAsync(Guid sessionId, Guid userId, bool isRaised, CancellationToken ct = default);
    Task AskQuestionAsync(Guid sessionId, Guid userId, string userName, string questionText, CancellationToken ct = default);
    Task AnswerQuestionAsync(Guid questionId, Guid teacherId, string answerText, CancellationToken ct = default);

    Task<PagedResult<ChatMessageResponse>> GetChatHistoryPagedAsync(
        Guid sessionId, Guid userId, int page, int pageSize, CancellationToken ct = default);

    Task<PagedResult<QuestionResponse>> GetQuestionsPagedAsync(
        Guid sessionId, Guid userId, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Refuses anyone who does not belong in this lecture. Used by the SignalR hub before it puts a
    /// connection into the session's broadcast group — that group receives every chat message,
    /// reaction, hand-raise and participant count for the session, live, and joining it was open to
    /// any authenticated connection that knew a session id.
    /// </summary>
    Task EnsureCanWatchAsync(Guid sessionId, Guid userId, CancellationToken ct = default);
}
