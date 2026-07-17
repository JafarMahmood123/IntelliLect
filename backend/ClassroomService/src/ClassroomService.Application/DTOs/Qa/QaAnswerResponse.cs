namespace ClassroomService.Application.DTOs.Qa;

/// <summary>
/// A grounded, cited answer for a classroom Q&amp;A question. <see cref="HasAnswer"/>
/// is false when retrieval found no relevant material (the answer text then carries
/// a clear "no relevant material" message and there are no sources).
/// </summary>
public record QaAnswerResponse(string Answer, IReadOnlyList<QaSourceDto> Sources, bool HasAnswer);

/// <summary>A citation the student can verify against the material. No s3 keys / internals.</summary>
public record QaSourceDto(
    int Citation,
    Guid DocumentId,
    int? Page,
    int? Slide,
    string? Section);
