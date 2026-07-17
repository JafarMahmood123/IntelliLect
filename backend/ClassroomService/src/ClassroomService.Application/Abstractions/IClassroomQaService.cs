using ClassroomService.Application.DTOs.Qa;

namespace ClassroomService.Application.Abstractions;

public interface IClassroomQaService
{
    /// <summary>
    /// Answer a member's question grounded in one classroom's material. The retrieval scope is
    /// <paramref name="classroomId"/> (from the route) plus verified membership — never a client
    /// value. Missing classroom -> 404; non-member -> 403; empty question -> 422.
    /// </summary>
    Task<QaAnswerResponse> AnswerAsync(
        Guid classroomId, Guid requestingUserId, string question, CancellationToken ct = default);
}
