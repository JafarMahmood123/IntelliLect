namespace UserManagementService.Application.Abstractions;

public interface IEmailBodyFactory
{
    string CreatePasswordResetBody(string code);
}