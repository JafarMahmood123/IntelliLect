using AutoMapper;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;
using UserManagementService.Application.Common.Users;
using UserManagementService.Application.DTOs.User;

namespace UserManagementService.Application.UserDirectory;

public sealed class UserDirectoryService : IUserDirectoryService
{
    private readonly IUserRepository _userRepository;
    private readonly IClassroomInternalClient _classroomClient;
    private readonly IMapper _mapper;

    public UserDirectoryService(
        IUserRepository userRepository,
        IClassroomInternalClient classroomClient,
        IMapper mapper)
    {
        _userRepository = userRepository;
        _classroomClient = classroomClient;
        _mapper = mapper;
    }

    // Steps 3-5: return the page of users matching the search/filter criteria. When nothing
    // matches (alternate path 5أ), this is simply an empty page — not an error.
    public async Task<PagedResult<UserResponse>> SearchUsersAsync(SearchUsersRequest request, CancellationToken ct = default)
    {
        var specification = UserQuerySpecification.Create(request);
        var (users, totalCount) = await _userRepository.SearchUsersAsync(specification, ct);
        var items = _mapper.Map<List<UserResponse>>(users);

        return new PagedResult<UserResponse>(items, totalCount, specification.Page, specification.PageSize);
    }

    // Steps 6-7: the user's full details plus the classrooms they teach or are enrolled in.
    public async Task<UserDetailResponse> GetUserDetailAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _userRepository.GetByIdAsync(userId, ct);

        // Alternate path 7أ: the requested account does not exist.
        if (user == null)
        {
            throw new NotFoundException("User not found.");
        }

        var profile = _mapper.Map<UserResponse>(user);

        // Alternate path 7ب: classroom memberships live in another service. If that call
        // fails, still return the core profile and flag memberships as unavailable rather
        // than failing the whole request.
        try
        {
            var classrooms = await _classroomClient.GetUserClassroomsAsync(userId, ct);
            return new UserDetailResponse(profile, classrooms.Teaching, classrooms.Enrolled, MembershipsUnavailable: false);
        }
        catch (Exception)
        {
            // Cross-service call failed after its own retries; degrade gracefully (7ب).
            return new UserDetailResponse(
                profile,
                UserClassrooms.Empty.Teaching,
                UserClassrooms.Empty.Enrolled,
                MembershipsUnavailable: true);
        }
    }
}
