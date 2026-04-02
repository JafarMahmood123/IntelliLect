namespace UserManagementService.Application.Abstractions;

public interface IResetPasswordTokenGenerator
{
    string Generate();
}
