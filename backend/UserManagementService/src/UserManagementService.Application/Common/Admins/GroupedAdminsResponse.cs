namespace UserManagementService.Application.Common.Admins;

public sealed class GroupedAdminsResponse
{
    public List<AdminStatusGroupResult> Groups { get; }
    public int TotalCount { get; }
    public int PageNumber { get; }
    public int PageSize { get; }
    public int TotalPages { get; }

    public GroupedAdminsResponse(List<AdminStatusGroupResult> groups, int totalCount, int pageNumber, int pageSize)
    {
        Groups = groups;
        TotalCount = totalCount;
        PageNumber = pageNumber;
        PageSize = pageSize;
        TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
    }

    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < TotalPages;
}
