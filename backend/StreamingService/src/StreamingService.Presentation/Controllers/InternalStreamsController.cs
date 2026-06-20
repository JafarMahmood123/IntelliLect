using Microsoft.AspNetCore.Mvc;
using StreamingService.Application.Abstractions;
using StreamingService.Domain.Entities;

namespace StreamingService.Presentation.Controllers;

[ApiController]
[Route("api/internal/streams")]
public sealed class InternalStreamsController : ControllerBase
{
    private readonly IStreamRepository _streamRepository;

    public InternalStreamsController(IStreamRepository streamRepository)
    {
        _streamRepository = streamRepository;
    }

    [HttpPost]
    public async Task<IActionResult> InitializeStream([FromBody] InitializeStreamRequest request)
    {
        var exists = await _streamRepository.ExistsAsync(request.SessionId);
        if (exists) return Ok();

        var stream = new LiveStream
        {
            Id = Guid.NewGuid(),
            SessionId = request.SessionId,
            ClassroomId = request.ClassroomId,
            TeacherId = request.TeacherId,
            Status = StreamStatus.Live,
            StartedAtUtc = DateTime.UtcNow,
            StreamKey = Guid.NewGuid().ToString("N")
        };

        await _streamRepository.AddAsync(stream);
        await _streamRepository.SaveChangesAsync();

        return CreatedAtAction(nameof(InitializeStream), new { id = stream.Id });
    }
}

public record InitializeStreamRequest(Guid SessionId, Guid ClassroomId, Guid TeacherId);