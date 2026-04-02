using UserManagementService.Application.Abstractions;

namespace UserManagementService.Infrastructure.Services;

public sealed class EmailBodyFactory : IEmailBodyFactory
{
    public string CreatePasswordResetBody(string code)
    {
        return $@"
        <div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; border: 1px solid #e5e4e7; border-radius: 8px; padding: 20px;'>
            <div style='text-align: center; margin-bottom: 20px;'>
                <h1 style='color: #08060d;'>IntelliLect</h1>
            </div>
            <div style='color: #6b6375; line-height: 1.6;'>
                <p>Hello,</p>
                <p>We received a request to reset your password. Use the verification code below to proceed:</p>
                <div style='text-align: center; margin: 30px 0;'>
                    <span style='background-color: #f4f3ec; color: #aa3bff; font-size: 32px; font-weight: bold; letter-spacing: 5px; padding: 10px 20px; border-radius: 4px; border: 2px dashed #aa3bff;'>
                        {code}
                    </span>
                </div>
                <p>This code is valid for <b>15 minutes</b>. If you did not request a password reset, please ignore this email.</p>
            </div>
            <hr style='border: 0; border-top: 1px solid #e5e4e7; margin: 20px 0;' />
            <div style='text-align: center; font-size: 12px; color: #9ca3af;'>
                <p>&copy; {DateTime.UtcNow.Year} IntelliLect Graduation Project. All rights reserved.</p>
            </div>
        </div>";
    }
}