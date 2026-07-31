namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Server-owned limits for in-session quizzes.
///
/// These are enforced on every write AND delivered to the browser, for the same reason
/// StreamingService's IMediaSettings exists: Vite substitutes import.meta.env at build time, so a
/// limit duplicated in frontend config is baked into the bundle and drifts from the API the first
/// time either side changes. The teacher's "how many questions" selector reads its bounds from
/// here, so it can never offer a value the server will reject.
/// </summary>
public interface IQuizSettings
{
    /// <summary>Upper bound on questions in one quiz.</summary>
    int MaxQuestionsPerQuiz { get; }

    /// <summary>Lower bound on options per question. Below 2 it is not a choice.</summary>
    int MinAnswersPerQuestion { get; }

    /// <summary>Upper bound on options per question.</summary>
    int MaxAnswersPerQuestion { get; }

    /// <summary>Pre-filled time for a new question, in seconds.</summary>
    int DefaultSecondsPerQuestion { get; }

    /// <summary>Ceiling on a whole quiz's computed duration, so a mistyped time cannot run for hours.</summary>
    int MaxQuizDurationSeconds { get; }

    /// <summary>
    /// How long past the deadline a submission is still accepted. An answer clicked at T-0.5s can
    /// arrive at T+0.3s; rejecting it punishes network latency rather than the student.
    /// </summary>
    int LateAnswerGraceSeconds { get; }
}
