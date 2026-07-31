namespace StreamingService.Application.DTOs;

/// <summary>Teacher's in-session recording toggle. False stops recording, which is final.</summary>
public record UpdateRecordingRequest(bool Enabled);
