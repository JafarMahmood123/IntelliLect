namespace ClassroomService.Application.Exceptions;

/// <summary>
/// Thrown when an uploaded file exceeds the configured size limit.
/// Maps to HTTP 413 Payload Too Large.
///
/// Distinct from <see cref="ValidationException"/> (422) because the client can act on it
/// differently: 422 means "fix the content", 413 means "the same content, smaller".
/// </summary>
public sealed class PayloadTooLargeException : Exception
{
    public PayloadTooLargeException(string message) : base(message) { }
}
