namespace EmailService.Application.Abstractions;

public interface IEmailBodyFactory
{
    string CreatePasswordResetBody(string code);
    string CreateTwoFactorCodeBody(string code);
    string CreateStatusChangedBody(string firstName, string status);
    string CreateTeacherChangedBody(string firstName, string classroomName, bool isNewTeacher);
}
