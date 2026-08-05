using System.Net;
using EmailService.Application.Abstractions;

namespace EmailService.Infrastructure.Services;

public sealed class EmailBodyFactory : IEmailBodyFactory
{
    /// <summary>
    /// Everything interpolated into these templates is HTML-encoded first.
    ///
    /// The values are not ours. A first name comes from whatever someone typed into the
    /// registration form, and a classroom name from whatever a teacher called it — so an
    /// unencoded interpolation puts attacker-controlled markup inside an email that carries our
    /// name and branding. Mail clients mostly refuse to run script, but they render links and
    /// images perfectly well, which is all a convincing phishing line needs.
    ///
    /// Applied to the codes too, though they are server-generated digits. A rule with an exception
    /// is a rule someone eventually forgets to apply.
    /// </summary>
    private static string Safe(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    public string CreatePasswordResetBody(string code)
    {
        var safeCode = Safe(code);
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
                        {safeCode}
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

    public string CreateTwoFactorCodeBody(string code)
    {
        var safeCode = Safe(code);
        return $@"
        <div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; border: 1px solid #e5e4e7; border-radius: 8px; padding: 20px;'>
            <div style='text-align: center; margin-bottom: 20px;'>
                <h1 style='color: #08060d;'>IntelliLect</h1>
            </div>
            <div style='color: #6b6375; line-height: 1.6;'>
                <p>Hello,</p>
                <p>A sign-in attempt requires a second verification step. Use the code below to complete your login:</p>
                <div style='text-align: center; margin: 30px 0;'>
                    <span style='background-color: #f4f3ec; color: #aa3bff; font-size: 32px; font-weight: bold; letter-spacing: 5px; padding: 10px 20px; border-radius: 4px; border: 2px dashed #aa3bff;'>
                        {safeCode}
                    </span>
                </div>
                <p>This code is valid for <b>5 minutes</b>. If you did not attempt to sign in, please secure your account and ignore this email.</p>
            </div>
            <hr style='border: 0; border-top: 1px solid #e5e4e7; margin: 20px 0;' />
            <div style='text-align: center; font-size: 12px; color: #9ca3af;'>
                <p>&copy; {DateTime.UtcNow.Year} IntelliLect Graduation Project. All rights reserved.</p>
            </div>
        </div>";
    }

    public string CreateStatusChangedBody(string firstName, string status)
    {
        var safeFirstName = Safe(firstName);
        var (title, message) = status.ToLowerInvariant() switch
        {
            "pending" => ("Welcome!", "We have received your registration request. An administrator will review your details shortly."),
            "active" => ("Account Approved!", "Great news! Your account has been approved. You can now log in and access all features."),
            "rejected" => ("Request Update", "After reviewing your registration request, we are unable to approve your account at this time."),
            "deactivated" => ("Account Deactivated", "Your account has been deactivated. If you believe this is a mistake, please contact support."),
            _ => ("Account Update", "There has been an update to your account status.")
        };

        return $@"
        <div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; border: 1px solid #e5e4e7; border-radius: 8px; padding: 20px;'>
            <h1 style='color: #08060d;'>IntelliLect</h1>
            <h2 style='color: #aa3bff;'>{title}</h2>
            <p>Hello {safeFirstName},</p>
            <p>{message}</p>
            <hr style='border: 0; border-top: 1px solid #e5e4e7; margin: 20px 0;' />
            <p style='font-size: 12px; color: #9ca3af;'>&copy; {DateTime.UtcNow.Year} IntelliLect Team</p>
        </div>";
    }

    public string CreateTeacherChangedBody(string firstName, string classroomName, bool isNewTeacher)
    {
        var safeFirstName = Safe(firstName);
        var safeClassroomName = Safe(classroomName);
        var (title, message) = isNewTeacher
            ? ("You Have a New Classroom",
               $"You have been assigned as the teacher of the classroom \"{safeClassroomName}\". You can now manage it and hold its sessions.")
            : ("Classroom Reassigned",
               $"The classroom \"{safeClassroomName}\" has been reassigned to another teacher, and it is no longer under your management. Its content and outputs are unchanged.");

        return $@"
        <div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; border: 1px solid #e5e4e7; border-radius: 8px; padding: 20px;'>
            <h1 style='color: #08060d;'>IntelliLect</h1>
            <h2 style='color: #aa3bff;'>{title}</h2>
            <p>Hello {safeFirstName},</p>
            <p>{message}</p>
            <hr style='border: 0; border-top: 1px solid #e5e4e7; margin: 20px 0;' />
            <p style='font-size: 12px; color: #9ca3af;'>&copy; {DateTime.UtcNow.Year} IntelliLect Team</p>
        </div>";
    }

    public string CreateMembershipChangedBody(string firstName, string classroomName, bool isAdded)
    {
        var safeFirstName = Safe(firstName);
        var safeClassroomName = Safe(classroomName);
        var (title, message) = isAdded
            ? ("Added to a Classroom",
               $"You have been added to the classroom \"{safeClassroomName}\". You can now access its sessions and materials.")
            : ("Removed from a Classroom",
               $"You have been removed from the classroom \"{safeClassroomName}\" and no longer have access to it.");

        return $@"
        <div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; border: 1px solid #e5e4e7; border-radius: 8px; padding: 20px;'>
            <h1 style='color: #08060d;'>IntelliLect</h1>
            <h2 style='color: #aa3bff;'>{title}</h2>
            <p>Hello {safeFirstName},</p>
            <p>{message}</p>
            <hr style='border: 0; border-top: 1px solid #e5e4e7; margin: 20px 0;' />
            <p style='font-size: 12px; color: #9ca3af;'>&copy; {DateTime.UtcNow.Year} IntelliLect Team</p>
        </div>";
    }
}
