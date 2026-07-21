namespace IntelliLect.Contracts.Messages;

/// <summary>
/// Notifies a student that their membership in a classroom changed (use-case "إدارة أعضاء الفصل
/// الدراسي", step 7). <see cref="IsAdded"/> is true when the student was added, false when removed.
/// </summary>
public sealed record ClassroomMembershipChangedMessage(
    string Email,
    string FirstName,
    string ClassroomName,
    bool IsAdded);
