using Microsoft.Extensions.Logging;
using StreamingService.Application.Abstractions;
using StreamingService.Application.DTOs;
using StreamingService.Application.DTOs.Chat;
using StreamingService.Application.DTOs.Question;
using StreamingService.Domain.Entities;

namespace StreamingService.Application.Services;

public sealed class InteractionService : IInteractionService
{
    private readonly IStreamChatMessageRepository _chatRepository;
    private readonly IRepository<StreamReaction> _reactionRepository;
    private readonly IStreamQuestionRepository _questionRepository;
    private readonly IParticipantRepository _participantRepository;
    private readonly IStreamRepository _streamRepository;
    private readonly IStreamHubContext _hubContext;
    private readonly ILogger<InteractionService> _logger;

    public InteractionService(
        IStreamChatMessageRepository chatRepository,
        IRepository<StreamReaction> reactionRepository,
        IStreamQuestionRepository questionRepository,
        IParticipantRepository participantRepository,
        IStreamRepository streamRepository,
        IStreamHubContext hubContext,
        ILogger<InteractionService> logger)
    {
        _chatRepository = chatRepository;
        _reactionRepository = reactionRepository;
        _questionRepository = questionRepository;
        _participantRepository = participantRepository;
        _streamRepository = streamRepository;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task SendChatMessageAsync(Guid sessionId, Guid userId, string userName, string message, CancellationToken ct)
    {
        var stream = await _streamRepository.GetBySessionIdAsync(sessionId, false, ct);
        if (stream == null || stream.Status != StreamStatus.Live)
        {
            _logger.LogWarning(
                "Chat message rejected: stream inactive or missing for SessionId: {SessionId}, UserId: {UserId}",
                sessionId, userId);
            throw new InvalidOperationException("Stream is not active.");
        }

        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Message cannot be empty.");

        var chat = new StreamChatMessage
        {
            Id = Guid.NewGuid(),
            StreamId = stream.Id,
            UserId = userId,
            UserName = userName,
            Message = message.Trim()
        };

        await _chatRepository.AddAsync(chat, ct);
        await _chatRepository.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Chat message sent for SessionId: {SessionId}, UserId: {UserId}, MessageLength: {MessageLength}",
            sessionId, userId, chat.Message.Length);

        await _hubContext.BroadcastChatMessageAsync(sessionId, chat.UserId, chat.UserName, chat.Message);
    }

    public async Task SendReactionAsync(Guid sessionId, Guid userId, string emoji, CancellationToken ct)
    {
        var stream = await _streamRepository.GetBySessionIdAsync(sessionId, false, ct);
        if (stream == null || stream.Status != StreamStatus.Live)
        {
            _logger.LogWarning(
                "Reaction rejected: stream inactive or missing for SessionId: {SessionId}, UserId: {UserId}",
                sessionId, userId);
            throw new InvalidOperationException("Stream is not active.");
        }

        if (string.IsNullOrWhiteSpace(emoji))
            throw new ArgumentException("Emoji cannot be empty.");

        var reaction = new StreamReaction
        {
            Id = Guid.NewGuid(),
            StreamId = stream.Id,
            UserId = userId,
            Emoji = emoji.Trim()
        };

        await _reactionRepository.AddAsync(reaction, ct);
        await _reactionRepository.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Reaction sent for SessionId: {SessionId}, UserId: {UserId}",
            sessionId, userId);

        await _hubContext.BroadcastReactionAsync(sessionId, reaction.UserId, reaction.Emoji);
    }

    public async Task AskQuestionAsync(Guid sessionId, Guid userId, string userName, string questionText, CancellationToken ct)
    {
        var stream = await _streamRepository.GetBySessionIdAsync(sessionId, false, ct);
        if (stream == null || stream.Status != StreamStatus.Live)
        {
            _logger.LogWarning(
                "Question rejected: stream inactive or missing for SessionId: {SessionId}, UserId: {UserId}",
                sessionId, userId);
            throw new InvalidOperationException("Stream is not active.");
        }

        if (string.IsNullOrWhiteSpace(questionText))
            throw new ArgumentException("Question text cannot be empty.");

        var question = new StreamQuestion
        {
            Id = Guid.NewGuid(),
            StreamId = stream.Id,
            UserId = userId,
            UserName = userName,
            QuestionText = questionText.Trim()
        };

        await _questionRepository.AddAsync(question, ct);
        await _questionRepository.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Question asked for SessionId: {SessionId}, UserId: {UserId}, QuestionId: {QuestionId}",
            sessionId, userId, question.Id);
    }

    public async Task AnswerQuestionAsync(Guid questionId, Guid teacherId, string answerText, CancellationToken ct)
    {
        var question = await _questionRepository.GetByIdAsync(questionId, ct);
        if (question == null || question.IsAnswered)
        {
            _logger.LogWarning(
                "Answer rejected: question not found or already answered. QuestionId: {QuestionId}",
                questionId);
            throw new InvalidOperationException("Question not found or already answered.");
        }

        var stream = await _streamRepository.GetByIdAsync(question.StreamId, ct);
        if (stream == null || stream.TeacherId != teacherId)
        {
            _logger.LogWarning(
                "Answer rejected: unauthorized. QuestionId: {QuestionId}, TeacherId: {TeacherId}",
                questionId, teacherId);
            throw new UnauthorizedAccessException("Only the teacher can answer questions.");
        }

        question.AnswerText = answerText?.Trim();
        question.IsAnswered = true;
        question.AnsweredAtUtc = DateTime.UtcNow;

        await _questionRepository.UpdateAsync(question, ct);
        await _questionRepository.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Question answered. QuestionId: {QuestionId}, TeacherId: {TeacherId}, SessionId: {SessionId}",
            questionId, teacherId, stream.SessionId);
    }

    public async Task ToggleHandRaiseAsync(Guid sessionId, Guid userId, bool isRaised, CancellationToken ct)
    {
        var participant = await _participantRepository.GetBySessionAndUserAsync(sessionId, userId, ct);
        if (participant == null)
        {
            _logger.LogWarning(
                "Hand raise toggle failed: user not a participant. SessionId: {SessionId}, UserId: {UserId}",
                sessionId, userId);
            throw new InvalidOperationException("Not a participant in this stream.");
        }

        participant.IsHandRaised = isRaised;
        await _participantRepository.UpdateAsync(participant, ct);
        await _participantRepository.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Hand raise toggled for SessionId: {SessionId}, UserId: {UserId}, IsRaised: {IsRaised}",
            sessionId, userId, isRaised);

        await _hubContext.NotifyHandRaisedAsync(sessionId, userId, isRaised);
    }

    public async Task<PagedResult<ChatMessageResponse>> GetChatHistoryPagedAsync(Guid sessionId, int page, int pageSize, CancellationToken ct)
    {
        var stream = await _streamRepository.GetBySessionIdAsync(sessionId, false, ct);
        if (stream == null)
        {
            _logger.LogWarning("Chat history requested for missing stream. SessionId: {SessionId}", sessionId);
            throw new KeyNotFoundException("Stream not found.");
        }

        var (items, totalCount) = await _chatRepository.GetByStreamIdPagedAsync(stream.Id, page, pageSize, ct);

        _logger.LogDebug(
            "Chat history fetched for SessionId: {SessionId}, Page: {Page}, PageSize: {PageSize}, TotalCount: {TotalCount}",
            sessionId, page, pageSize, totalCount);

        var responses = items.Select(m => new ChatMessageResponse(
            m.Id,
            m.UserId,
            m.UserName,
            m.Message,
            m.SentAtUtc
        )).ToList();

        return new PagedResult<ChatMessageResponse>(responses, totalCount, page, pageSize);
    }

    public async Task<PagedResult<QuestionResponse>> GetQuestionsPagedAsync(Guid sessionId, int page, int pageSize, CancellationToken ct)
    {
        var stream = await _streamRepository.GetBySessionIdAsync(sessionId, false, ct);
        if (stream == null)
        {
            _logger.LogWarning("Questions requested for missing stream. SessionId: {SessionId}", sessionId);
            throw new KeyNotFoundException("Stream not found.");
        }

        var (items, totalCount) = await _questionRepository.GetByStreamIdPagedAsync(stream.Id, page, pageSize, ct);

        _logger.LogDebug(
            "Questions fetched for SessionId: {SessionId}, Page: {Page}, PageSize: {PageSize}, TotalCount: {TotalCount}",
            sessionId, page, pageSize, totalCount);

        var responses = items.Select(q => new QuestionResponse(
            q.Id,
            q.UserId,
            q.UserName,
            q.QuestionText,
            q.AnswerText,
            q.IsAnswered,
            q.AskedAtUtc,
            q.AnsweredAtUtc
        )).ToList();

        return new PagedResult<QuestionResponse>(responses, totalCount, page, pageSize);
    }
}
