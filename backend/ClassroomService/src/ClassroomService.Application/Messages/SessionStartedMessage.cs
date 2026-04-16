namespace ClassroomService.Application.Messages;

public record SessionStartedMessage(Guid SessionId, Guid ClassroomId);