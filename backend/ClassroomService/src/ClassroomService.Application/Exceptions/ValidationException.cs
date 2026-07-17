namespace ClassroomService.Application.Exceptions;

/// <summary>
/// Thrown when a request body fails semantic validation (e.g. an empty question).
/// Maps to HTTP 422 Unprocessable Entity.
/// </summary>
public sealed class ValidationException : Exception
{
    public ValidationException(string message) : base(message) { }
}
