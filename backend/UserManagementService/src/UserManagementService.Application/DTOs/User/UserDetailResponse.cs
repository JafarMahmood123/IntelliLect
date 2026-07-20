using UserManagementService.Application.Abstractions;

namespace UserManagementService.Application.DTOs.User;

/// <summary>
/// A user's full profile for the super admin detail view, together with the classrooms
/// they teach or are enrolled in.
/// </summary>
public sealed record UserDetailResponse(
    UserResponse User,
    IReadOnlyList<ClassroomSummary> Teaching,
    IReadOnlyList<ClassroomSummary> Enrolled,
    // Alternate path 7ب: true when the classroom memberships could not be loaded, so the
    // client can show the rest of the details and flag memberships as temporarily unavailable.
    bool MembershipsUnavailable);
