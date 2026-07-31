namespace StreamingService.Application.Abstractions;

public interface IStreamClient
{
    Task ReceiveHandRaise(Guid userId, bool isRaised);
    Task UpdateParticipantCount(int count);
    Task StreamStatusChanged(string status);
    /// <summary>The teacher changed whether students may publish audio/video. Every client updates
    /// its controls; the media server has already enforced it on connected students.</summary>
    Task PublishPolicyChanged(bool canPublishAudio, bool canPublishVideo);
    /// <summary>The session's recording state changed ("Off"/"Recording"/"Ended"). Sent to students
    /// as well as the teacher: being recorded is something every participant should be able to
    /// see.</summary>
    Task RecordingStateChanged(string state);
    /// <summary>
    /// A quiz in this session changed state ("Open"/"Closed"/"Cancelled"). Carries the id ONLY —
    /// each client then fetches the view it is entitled to, which is what makes it impossible for
    /// the answer key to reach a student over the socket.
    /// </summary>
    Task QuizChanged(Guid quizId, string state);
    Task ReceiveChatMessage(Guid userId, string userName, string message);
    Task ReceiveReaction(Guid userId, string emoji);
}