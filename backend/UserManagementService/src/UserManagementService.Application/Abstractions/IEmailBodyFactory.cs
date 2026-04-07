using UserManagementService.Domain.Entities;

namespace UserManagementService.Application.Abstractions;

public interface IEmailBodyFactory
{
    string CreatePasswordResetBody(string code);
    string CreateStatusChangedBody(string firstName, UserStatus status);
}