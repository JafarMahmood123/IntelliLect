namespace UserManagementService.Application.DTOs.User;

/// <summary>
/// Query parameters for the super admin user directory: a free-text term matched across
/// name/username/email, plus filters by role, status, and creation date, with paging/sorting.
/// </summary>
public sealed class SearchUsersRequest
{
    public string? SearchTerm { get; set; }
    public string? Role { get; set; }
    public string? Status { get; set; }
    public DateTime? CreatedFrom { get; set; }
    public DateTime? CreatedTo { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public string? SortBy { get; set; }
    public string? SortDirection { get; set; }
}
