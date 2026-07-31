namespace StreamingService.Application.DTOs;

/// <summary>
/// The session's recording state, as the name of the RecordingState enum member ("Off",
/// "Recording", "Ended"). Sent as a string rather than a bool because clients need to tell "not
/// recording, can be started" from "not recording, already finished" — the latter must render a
/// disabled control rather than an inviting one.
/// </summary>
public record RecordingStateResponse(string State);
