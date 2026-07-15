namespace ClassroomService.Application.Exceptions;

/// <summary>
/// Thrown when an authenticated user is not permitted to access a resource (e.g. they are not a
/// member of the classroom). Mapped to HTTP 403 by the global exception handler — distinct from
/// <see cref="UnauthorizedAccessException"/> (401, "not authenticated").
/// </summary>
public sealed class ForbiddenAccessException : Exception
{
    public ForbiddenAccessException(string message) : base(message) { }
}
