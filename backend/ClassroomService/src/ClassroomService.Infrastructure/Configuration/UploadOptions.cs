using ClassroomService.Application.Abstractions;

namespace ClassroomService.Infrastructure.Configuration;

/// <summary>
/// Classroom material upload limits, bound from the "Uploads" section. Same port/adapter shape as
/// <see cref="QuizOptions"/>: the concrete options object implements the Application-layer settings
/// interface, so nothing above Infrastructure has to know where the values came from.
/// </summary>
public sealed class UploadOptions : IUploadSettings
{
    public const string SectionName = "Uploads";

    /// <summary>
    /// 50 MB. Comfortable for a lecture deck full of images, small enough that a video dropped in
    /// by mistake is refused rather than sitting in object storage forever.
    /// </summary>
    public long MaxFileSizeBytes { get; init; } = 50L * 1024 * 1024;

    /// <summary>8 KB. Multipart framing is a few hundred bytes; this is deliberate slack.</summary>
    public long MultipartOverheadBytes { get; init; } = 8L * 1024;

    /// <summary>
    /// Mirrors RagService's extractor content types (`_support.py`). Keep the two in step:
    /// a type accepted here but unknown there uploads fine and never indexes.
    /// </summary>
    public string[] AllowedContentTypes { get; init; } =
    [
        "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "text/plain",
        "text/markdown",
        "text/x-markdown",
    ];

    /// <summary>Mirrors RagService's extractor extensions (`_support.py`).</summary>
    public string[] AllowedExtensions { get; init; } =
    [
        "pdf",
        "docx",
        "pptx",
        "txt",
        "md",
        "markdown",
    ];

    IReadOnlyCollection<string> IUploadSettings.AllowedContentTypes => AllowedContentTypes;

    IReadOnlyCollection<string> IUploadSettings.AllowedExtensions => AllowedExtensions;
}
