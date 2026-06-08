namespace IntelliLect.Contracts.Messages;

public sealed record SessionStartedMessage(Guid SessionId, Guid ClassroomId);