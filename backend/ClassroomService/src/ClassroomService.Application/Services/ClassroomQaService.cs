using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Qa;
using ClassroomService.Application.Exceptions;
using Microsoft.Extensions.Logging;

namespace ClassroomService.Application.Services;

/// <summary>
/// User-facing Q&amp;A over a classroom's material. Authenticates + authorizes membership, then
/// runs RagService's grounded RAG answer server-side (the internal secret stays inside the
/// internal client). The retrieval scope is always the route classroom id + verified membership,
/// so a client can never ask about a classroom it is not in.
/// </summary>
public sealed class ClassroomQaService : IClassroomQaService
{
    private readonly IClassroomRepository _classroomRepository;
    private readonly IMembershipRepository _membershipRepository;
    private readonly IRagInternalClient _knowledgeClient;
    private readonly ILogger<ClassroomQaService> _logger;

    public ClassroomQaService(
        IClassroomRepository classroomRepository,
        IMembershipRepository membershipRepository,
        IRagInternalClient knowledgeClient,
        ILogger<ClassroomQaService> logger)
    {
        _classroomRepository = classroomRepository;
        _membershipRepository = membershipRepository;
        _knowledgeClient = knowledgeClient;
        _logger = logger;
    }

    public async Task<QaAnswerResponse> AnswerAsync(
        Guid classroomId, Guid requestingUserId, string question, CancellationToken ct = default)
    {
        // Reject empty input before any authz/inference work -> 422.
        var cleaned = question?.Trim() ?? string.Empty;
        if (cleaned.Length == 0)
        {
            throw new ValidationException("Question must not be empty.");
        }

        // Members only: missing classroom -> 404, non-member -> 403.
        await EnsureMemberAsync(classroomId, requestingUserId, ct);

        // Scope is the PATH classroom id + membership — the client cannot influence it.
        var result = await _knowledgeClient.GetAnswerAsync(classroomId, cleaned, ct);

        var sources = result.Sources
            .Select(s => new QaSourceDto(s.Citation, s.DocumentId, s.Page, s.Slide, s.Section))
            .ToList();

        // No cited sources => retrieval found nothing relevant; don't claim an answer.
        var hasAnswer = sources.Count > 0;

        _logger.LogInformation(
            "Q&A answered for classroom {ClassroomId} by user {UserId}: hasAnswer={HasAnswer}, sources={SourceCount}.",
            classroomId, requestingUserId, hasAnswer, sources.Count);

        return new QaAnswerResponse(result.Answer, sources, hasAnswer);
    }

    /// <summary>
    /// Same membership rule as recordings/summaries: classroom teacher OR enrolled student.
    /// Missing classroom -> 404; non-member -> 403.
    /// </summary>
    /// <summary>
    /// Delegates to <see cref="ClassroomAccess.EnsureMemberAsync"/>. This was a private copy of
    /// that rule, identical to the four others in this service layer; see the reason there.
    /// </summary>
    private Task EnsureMemberAsync(Guid classroomId, Guid userId, CancellationToken ct)
        => ClassroomAccess.EnsureMemberAsync(
            _classroomRepository, _membershipRepository, classroomId, userId, ct);
}
