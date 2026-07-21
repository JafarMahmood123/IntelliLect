namespace ClassroomService.Application.DTOs.Classroom;

/// <summary>The current teacher and name of a classroom, used to detect the 4ب no-op and to
/// carry the classroom name back for the ownership-change notifications (step 6).</summary>
public sealed record ClassroomTeacherInfo(Guid TeacherId, string Name);

/// <summary>
/// Outcome of an ownership transfer. <see cref="Changed"/> is false for the 4ب no-op (the new
/// teacher already owns the classroom). <see cref="PreviousTeacherId"/> lets the caller notify the
/// teacher who lost the classroom.
/// </summary>
public sealed record ChangeTeacherResult(
    bool Changed,
    Guid PreviousTeacherId,
    Guid NewTeacherId,
    string ClassroomName);
