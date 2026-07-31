using System.Net.Http.Json;
using ClassroomService.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace ClassroomService.Infrastructure.Services;

/// <inheritdoc cref="IQuizNotifier"/>
public sealed class StreamingQuizNotifier : IQuizNotifier
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<StreamingQuizNotifier> _logger;

    public StreamingQuizNotifier(HttpClient httpClient, ILogger<StreamingQuizNotifier> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task QuizChangedAsync(
        Guid sessionId, Guid quizId, string state, CancellationToken ct = default)
    {
        try
        {
            await _httpClient.PostAsJsonAsync(
                $"http://streaming-service:8080/api/internal/streams/{sessionId}/quiz-event",
                new { QuizId = quizId, State = state },
                ct);
        }
        catch (Exception ex)
        {
            // Best-effort by design. The quiz is already committed, and every client re-reads state
            // when it next asks — so a missed push costs a delayed UI update, never correctness.
            _logger.LogWarning(
                ex,
                "Could not broadcast quiz {QuizId} state {State} to session {SessionId}; clients will "
                + "pick it up on their next read.",
                quizId, state, sessionId);
        }
    }
}
