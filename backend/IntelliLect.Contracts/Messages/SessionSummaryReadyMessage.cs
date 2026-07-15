namespace IntelliLect.Contracts.Messages;

/// <summary>
/// Published by KnowledgeService when a session's summary pipeline finishes (S-3): the Markdown
/// (S-1) and its PDF rendition (S-2) have been generated and uploaded to object storage. Carries
/// the outcome so ClassroomService (S-4) can store the summary metadata and serve downloads
/// (<c>Available</c> on success) or mark it <c>Failed</c>. Mirrors
/// <see cref="SessionRecordingReadyMessage"/>: a single message conveys success or failure via
/// <see cref="Succeeded"/>/<see cref="Error"/>, and fields after <see cref="GeneratedAt"/> are
/// optional with defaults so the contract stays backward compatible with earlier producers.
/// </summary>
public sealed record SessionSummaryReadyMessage(
    Guid SessionId,
    Guid ClassroomId,
    string MdS3Key,
    string PdfS3Key,
    DateTimeOffset GeneratedAt,
    bool Succeeded = true,
    string? Error = null);
