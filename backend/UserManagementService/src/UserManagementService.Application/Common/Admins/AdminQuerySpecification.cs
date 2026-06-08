using UserManagementService.Application.DTOs.Admin;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Application.Common.Admins;

public sealed class AdminQuerySpecification
{
    public const string CreatedAtSortField = "createdat";
    public const string UserNameSortField = "username";
    public const string EmailSortField = "email";
    public const string FirstNameSortField = "firstname";
    public const string LastNameSortField = "lastname";
    public const string StatusSortField = "status";
    public const string StatusGroupField = "status";

    private AdminQuerySpecification(
        int page,
        int pageSize,
        string sortBy,
        string sortDirection,
        string? groupBy,
        string? userName,
        string? email,
        string? firstName,
        string? lastName,
        UserStatus? status,
        DateTime? createdFrom,
        DateTime? createdTo)
    {
        Page = page;
        PageSize = pageSize;
        SortBy = sortBy;
        SortDirection = sortDirection;
        GroupBy = groupBy;
        UserName = userName;
        Email = email;
        FirstName = firstName;
        LastName = lastName;
        Status = status;
        CreatedFrom = createdFrom;
        CreatedTo = createdTo;
    }

    public int Page { get; }
    public int PageSize { get; }
    public string SortBy { get; }
    public string SortDirection { get; }
    public string? GroupBy { get; }
    public string? UserName { get; }
    public string? Email { get; }
    public string? FirstName { get; }
    public string? LastName { get; }
    public UserStatus? Status { get; }
    public DateTime? CreatedFrom { get; }
    public DateTime? CreatedTo { get; }

    public bool SortDescending => SortDirection == "desc";
    public bool GroupByStatus => GroupBy == StatusGroupField;

    public static AdminQuerySpecification Create(GetAdminsRequest request)
    {
        return new AdminQuerySpecification(
            NormalizePage(request.Page),
            NormalizePageSize(request.PageSize),
            NormalizeSortBy(request.SortBy),
            NormalizeSortDirection(request.SortDirection),
            NormalizeGroupBy(request.GroupBy),
            null,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    public static AdminQuerySpecification Create(SearchAdminsRequest request)
    {
        var createdFrom = request.CreatedFrom;
        var createdTo = request.CreatedTo;

        if (createdFrom.HasValue && createdTo.HasValue && createdFrom.Value > createdTo.Value)
        {
            throw new ArgumentException("CreatedFrom cannot be greater than CreatedTo.");
        }

        return new AdminQuerySpecification(
            NormalizePage(request.Page),
            NormalizePageSize(request.PageSize),
            NormalizeSortBy(request.SortBy),
            NormalizeSortDirection(request.SortDirection),
            null,
            NormalizeFilter(request.UserName),
            NormalizeFilter(request.Email),
            NormalizeFilter(request.FirstName),
            NormalizeFilter(request.LastName),
            NormalizeStatus(request.Status),
            createdFrom,
            createdTo);
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

    private static string? NormalizeGroupBy(string? groupBy)
    {
        if (string.IsNullOrWhiteSpace(groupBy))
        {
            return null;
        }

        var normalized = groupBy.Trim().ToLowerInvariant();
        return normalized == StatusGroupField
            ? StatusGroupField
            : throw new ArgumentException("Invalid groupBy value.");
    }

    private static string? NormalizeFilter(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
