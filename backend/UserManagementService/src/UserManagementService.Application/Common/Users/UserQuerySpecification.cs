using UserManagementService.Application.DTOs.User;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Application.Common.Users;

/// <summary>
/// Normalized, validated query for the super admin user directory. Built from a
/// <see cref="SearchUsersRequest"/>; guards paging, sorting, and enum parsing so the
/// repository receives only clean values.
/// </summary>
public sealed class UserQuerySpecification
{
    public const string CreatedAtSortField = "createdat";
    public const string UserNameSortField = "username";
    public const string EmailSortField = "email";
    public const string FirstNameSortField = "firstname";
    public const string LastNameSortField = "lastname";
    public const string StatusSortField = "status";
    public const string RoleSortField = "role";

    private UserQuerySpecification(
        int page,
        int pageSize,
        string sortBy,
        string sortDirection,
        string? searchTerm,
        RoleName? role,
        UserStatus? status,
        DateTime? createdFrom,
        DateTime? createdTo)
    {
        Page = page;
        PageSize = pageSize;
        SortBy = sortBy;
        SortDirection = sortDirection;
        SearchTerm = searchTerm;
        Role = role;
        Status = status;
        CreatedFrom = createdFrom;
        CreatedTo = createdTo;
    }

    public int Page { get; }
    public int PageSize { get; }
    public string SortBy { get; }
    public string SortDirection { get; }
    public string? SearchTerm { get; }
    public RoleName? Role { get; }
    public UserStatus? Status { get; }
    public DateTime? CreatedFrom { get; }
    public DateTime? CreatedTo { get; }

    public bool SortDescending => SortDirection == "desc";

    public static UserQuerySpecification Create(SearchUsersRequest request)
    {
        if (request.CreatedFrom.HasValue && request.CreatedTo.HasValue &&
            request.CreatedFrom.Value > request.CreatedTo.Value)
        {
            throw new ArgumentException("CreatedFrom cannot be greater than CreatedTo.");
        }

        return new UserQuerySpecification(
            NormalizePage(request.Page),
            NormalizePageSize(request.PageSize),
            NormalizeSortBy(request.SortBy),
            NormalizeSortDirection(request.SortDirection),
            NormalizeFilter(request.SearchTerm),
            NormalizeRole(request.Role),
            NormalizeStatus(request.Status),
            request.CreatedFrom,
            request.CreatedTo);
    }

    private static int NormalizePage(int page) => page < 1 ? 1 : page;

    private static int NormalizePageSize(int pageSize)
    {
        var normalizedPageSize = pageSize < 1 ? 10 : pageSize;
        return Math.Clamp(normalizedPageSize, 1, 100);
    }

    private static string NormalizeSortBy(string? sortBy)
    {
        var normalized = (sortBy ?? CreatedAtSortField)
            .Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized switch
        {
            CreatedAtSortField or "createdatutc" => CreatedAtSortField,
            UserNameSortField => UserNameSortField,
            EmailSortField => EmailSortField,
            FirstNameSortField => FirstNameSortField,
            LastNameSortField => LastNameSortField,
            StatusSortField => StatusSortField,
            RoleSortField => RoleSortField,
            _ => throw new ArgumentException("Invalid sortBy value.")
        };
    }

    private static string NormalizeSortDirection(string? sortDirection)
    {
        var normalized = (sortDirection ?? "desc").Trim().ToLowerInvariant();
        return normalized switch
        {
            "asc" => "asc",
            "desc" => "desc",
            _ => throw new ArgumentException("Invalid sortDirection value.")
        };
    }

    private static string? NormalizeFilter(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static RoleName? NormalizeRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return null;
        }

        return Enum.TryParse<RoleName>(role.Trim(), true, out var parsedRole)
            ? parsedRole
            : throw new ArgumentException("Invalid role value.");
    }

    private static UserStatus? NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        return Enum.TryParse<UserStatus>(status.Trim(), true, out var parsedStatus)
            ? parsedStatus
            : throw new ArgumentException("Invalid status value.");
    }
}
