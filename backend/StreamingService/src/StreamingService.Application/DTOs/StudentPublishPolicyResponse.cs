namespace StreamingService.Application.DTOs;

/// <summary>The current student publish policy for a session, returned after a teacher toggles it.</summary>
public record StudentPublishPolicyResponse(bool CanPublishAudio, bool CanPublishVideo);
