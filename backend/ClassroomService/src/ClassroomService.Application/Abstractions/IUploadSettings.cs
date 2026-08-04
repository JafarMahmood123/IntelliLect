namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Server-owned limits for classroom material uploads.
///
/// Same port/adapter shape as <see cref="IQuizSettings"/>, and delivered to the browser for the
/// same reason: a limit duplicated in frontend config is baked into the bundle at build time by
/// Vite and drifts from the API the first time either side changes. The upload control reads its
/// bounds from here, so it can never accept a file the server will reject.
///
/// Enforced in three places from this ONE value — the resource filter (before the body is read),
/// the file service (the exact per-file check), and nginx's client_max_body_size. nginx is the
/// only copy that cannot read this setting; see the note in nginx.conf.
/// </summary>
public interface IUploadSettings
{
    /// <summary>Largest accepted file, in bytes. The limit applies to the FILE, not the request.</summary>
    long MaxFileSizeBytes { get; }

    /// <summary>
    /// Slack added on top of <see cref="MaxFileSizeBytes"/> when checking a request's declared
    /// Content-Length, to cover multipart boundaries, part headers and the field name.
    ///
    /// Without it a file of exactly the maximum size would be refused, because its multipart
    /// envelope is a few hundred bytes larger than the file itself. The coarse early check uses
    /// max + this; the exact check uses the file's own length.
    /// </summary>
    long MultipartOverheadBytes { get; }

    /// <summary>
    /// Accepted MIME types. Defaults mirror exactly what RagService can extract — accepting
    /// a file no extractor handles buys an upload that can never be indexed, which is worse than a
    /// clear refusal at the door.
    /// </summary>
    IReadOnlyCollection<string> AllowedContentTypes { get; }

    /// <summary>
    /// Accepted file extensions, without the dot. Checked as an ALTERNATIVE to the content type,
    /// not in addition: browsers send an empty or generic type for some of these (notably .md),
    /// and RagService's own extractor router dispatches on either signal.
    /// </summary>
    IReadOnlyCollection<string> AllowedExtensions { get; }
}
