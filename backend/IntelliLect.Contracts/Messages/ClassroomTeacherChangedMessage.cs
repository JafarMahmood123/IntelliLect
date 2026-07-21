namespace IntelliLect.Contracts.Messages;

/// <summary>
/// Notifies a teacher that a classroom's ownership changed (use-case "إسناد معلم الفصل الدراسي أو
/// تغييره", step 6). Published once per teacher: <see cref="IsNewTeacher"/> is true for the teacher
/// who received the classroom and false for the teacher who lost it.
/// </summary>
public sealed record ClassroomTeacherChangedMessage(
    string Email,
    string FirstName,
    string ClassroomName,
    bool IsNewTeacher);
