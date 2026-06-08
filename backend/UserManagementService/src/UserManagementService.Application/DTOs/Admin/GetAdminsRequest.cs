namespace UserManagementService.Application.DTOs.Admin;

public sealed class GetAdminsRequest
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public string? SortBy { get; set; }
    public string? SortDirection { get; set; }
    public string? GroupBy { get; set; }
}
