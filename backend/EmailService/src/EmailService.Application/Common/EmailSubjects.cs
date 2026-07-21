namespace EmailService.Application.Common;

public static class EmailSubjects
{
    public const string RegistrationReceived = "Welcome to IntelliLect - Request Received";
    public const string AccountApproved = "Your IntelliLect Account is Now Active!";
    public const string AccountRejected = "Update Regarding Your Registration Request";
    public const string AccountDeactivated = "Your Account has been Deactivated";
    public const string PasswordReset = "Your Password Reset Code";
    public const string TwoFactorCode = "Your IntelliLect Login Verification Code";
    public const string ClassroomAssigned = "A Classroom Has Been Assigned to You";
    public const string ClassroomUnassigned = "A Classroom Has Been Reassigned";
}
