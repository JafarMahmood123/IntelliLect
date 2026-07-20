namespace UserManagementService.Application.Common;

/// <summary>Thrown when a requested entity does not exist. Maps to HTTP 404.</summary>
public sealed class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message) { }
}
