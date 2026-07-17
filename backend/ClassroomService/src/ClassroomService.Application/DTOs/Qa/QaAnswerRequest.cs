namespace ClassroomService.Application.DTOs.Qa;

/// <summary>
/// Q&amp;A request body. Note there is NO classroom id here — the retrieval scope is
/// taken from the route path plus the caller's verified membership, never trusted
/// from the client.
/// </summary>
public record QaAnswerRequest(string Question);
