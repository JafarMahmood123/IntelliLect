namespace StreamingService.Application.DTOs;

/// <summary>Teacher's request to set whether students may publish audio/video for a live session.</summary>
public record UpdatePublishPolicyRequest(bool CanPublishAudio, bool CanPublishVideo);
