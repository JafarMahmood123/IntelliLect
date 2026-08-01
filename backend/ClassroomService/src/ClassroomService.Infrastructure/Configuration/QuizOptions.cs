using ClassroomService.Application.Abstractions;

namespace ClassroomService.Infrastructure.Configuration;

/// <summary>
/// In-session quiz limits, bound from the "Quizzes" section. Same port/adapter shape as
/// <see cref="RecordingsOptions"/>: the concrete options object implements the Application-layer
/// settings interface, so nothing above Infrastructure has to know where the values came from.
/// </summary>
public sealed class QuizOptions : IQuizSettings
{
    public const string SectionName = "Quizzes";

    public int MaxQuestionsPerQuiz { get; init; } = 20;

    /// <summary>Two is the floor: a single-option question is not a question.</summary>
    public int MinAnswersPerQuestion { get; init; } = 2;

    public int MaxAnswersPerQuestion { get; init; } = 6;

    /// <summary>One minute, the agreed default for a newly added question.</summary>
    public int DefaultSecondsPerQuestion { get; init; } = 60;

    /// <summary>Two hours. Guards against a mistyped time limit rather than any real quiz.</summary>
    public int MaxQuizDurationSeconds { get; init; } = 7200;

    public int LateAnswerGraceSeconds { get; init; } = 3;

    /// <summary>
    /// How often to look for quizzes whose time has run out. Seconds, because a class that has just
    /// finished is waiting on it — Closed is what releases their marks.
    /// </summary>
    public int DeadlineSweepSeconds { get; init; } = 5;
}
