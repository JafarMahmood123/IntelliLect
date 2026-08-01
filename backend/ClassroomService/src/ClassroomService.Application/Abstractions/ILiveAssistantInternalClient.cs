namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Internal HTTP client for LiveAssistantService, which owns session transcripts (النص المفرّغ).
/// ClassroomService owns the session/recording/summary data but not the transcript, so deleting a
/// session's transcript is a cross-service call. Mirrors the other internal clients: sends the
/// shared secret and retries transient faults.
/// </summary>
public interface ILiveAssistantInternalClient
{
    /// <summary>
    /// Number of segments in a session's transcript, or null when there is no transcript (the
    /// service's 404). Used by the deletion impact preview (step 3). Throws on a hard failure so the
    /// caller can report the transcript status as temporarily unavailable rather than as absent.
    /// </summary>
    Task<int?> GetTranscriptSegmentCountAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Delete a session's transcript. Idempotent — a session with no transcript still succeeds
    /// (6أ). Retries transient faults and throws when exhausted, so a hard failure halts the
    /// session deletion with the session left PendingDeletion for a resumable re-run (6ب).
    /// </summary>
    Task DeleteSessionTranscriptAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Delete every transcript for a classroom (used by the classroom-deletion use-case so its
    /// sessions' transcripts do not outlive it). Retries then throws; returns the number removed.
    /// </summary>
    Task<int> DeleteClassroomTranscriptsAsync(Guid classroomId, CancellationToken ct = default);

    /// <summary>
    /// Ask the assistant for multiple-choice questions about the idea the teacher has just finished
    /// explaining. LiveAssistantService is the only service holding all three inputs — the live
    /// transcript, the brain, and retrieval over the classroom's material — so generation happens
    /// there and the proposal comes back here to become a Draft.
    /// </summary>
    /// <remarks>
    /// The option bounds are passed IN because this service owns the quiz limits; a second copy in
    /// the assistant's own settings would be a copy free to disagree with the one that actually
    /// rejects a publish.
    ///
    /// Throws <see cref="Exceptions.ConflictException"/> when the session has produced nothing to
    /// quiz on yet (the lecture has not said enough — the teacher fixes that by carrying on) and
    /// <see cref="Exceptions.ServiceUnavailableException"/> when the assistant could not produce a
    /// usable quiz. Kept distinct so the teacher is told which of the two happened.
    /// </remarks>
    Task<GeneratedQuizDto> GenerateQuizAsync(
        Guid sessionId,
        Guid classroomId,
        int questionCount,
        int minOptions,
        int maxOptions,
        IReadOnlyList<string>? avoid = null,
        CancellationToken ct = default);

    /// <summary>
    /// Write answer options for a question the TEACHER wrote, from that question plus the
    /// explanation they just gave and the classroom's material.
    /// </summary>
    /// <remarks>
    /// Throws the same two exceptions, meaning the same two things, as
    /// <see cref="GenerateQuizAsync"/>.
    /// </remarks>
    Task<GeneratedQuestionDto> GenerateAnswersAsync(
        Guid sessionId,
        Guid classroomId,
        string questionText,
        int minOptions,
        int maxOptions,
        CancellationToken ct = default);
}

/// <summary>An option the assistant proposed. Exactly one per question is correct.</summary>
public sealed record GeneratedOptionDto(string Text, bool IsCorrect);

/// <summary>
/// A point where the course material contradicted what the teacher said out loud.
///
/// The questions are written to match the MATERIAL, so a class is never marked against a slip.
/// This is how the teacher finds out that happened: an answer key that silently disagrees with
/// what they told the room a minute ago is worse than the mistake it fixes.
/// </summary>
public sealed record GeneratedCorrectionDto(string Taught, string Corrected);

/// <summary>A question the assistant proposed. Carries no marks or timing — those are the
/// teacher's to set, and this service supplies its own defaults.</summary>
public sealed record GeneratedQuestionDto(
    string Text,
    IReadOnlyList<GeneratedOptionDto> Options,
    IReadOnlyList<GeneratedCorrectionDto> Corrections);

/// <summary>
/// The assistant's proposal. <paramref name="Grounded"/> is false when no course material was
/// relevant and the questions came from the teacher's spoken words alone.
/// </summary>
public sealed record GeneratedQuizDto(
    string Title,
    bool Grounded,
    IReadOnlyList<GeneratedQuestionDto> Questions,
    IReadOnlyList<GeneratedCorrectionDto> Corrections);
