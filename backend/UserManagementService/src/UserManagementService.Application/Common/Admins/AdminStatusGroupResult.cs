namespace UserManagementService.Application.Common.Admins;

public sealed record AdminStatusGroupResult(
    string Status,
    List<AdminQueryResult> Items);
