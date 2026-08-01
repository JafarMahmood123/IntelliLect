namespace ClassroomService.Application.Exceptions;

/// <summary>
/// A dependency this request needed could not serve it right now, and retrying later may well
/// succeed. Distinct from <see cref="ConflictException"/>, which means the request cannot succeed
/// in the current state no matter how often it is retried — the difference decides whether the UI
/// should offer "try again" or explain what must change first.
/// </summary>
public sealed class ServiceUnavailableException : Exception
{
    public ServiceUnavailableException(string message) : base(message) { }

    public ServiceUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}
