using UserManagementService.Application.Abstractions;

namespace UserManagementService.Infrastructure.Authentication;

public class ResetPasswordTokenGenerator : IResetPasswordTokenGenerator
{
    public string Generate()
    {
        var random = new Random();
        var code = string.Empty;
        for (int i = 0; i < 6; i++)
        {
            code += random.Next(10).ToString();
        }

        return code;
    }
}
