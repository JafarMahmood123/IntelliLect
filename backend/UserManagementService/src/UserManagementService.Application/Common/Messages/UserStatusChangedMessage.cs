using UserManagementService.Domain.Entities;
namespace UserManagementService.Application.Common.Messages;

public record UserStatusChangedMessage(string Email, string FirstName, UserStatus NewStatus);