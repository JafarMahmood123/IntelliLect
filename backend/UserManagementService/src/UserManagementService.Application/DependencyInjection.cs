using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Authentication;
using UserManagementService.Application.ClassroomAdministration;
using UserManagementService.Application.KnowledgeAdministration;
using UserManagementService.Application.MemberAdministration;
using UserManagementService.Application.OutputAdministration;
using UserManagementService.Application.SessionMonitoring;
using UserManagementService.Application.SuperAdministration;
using UserManagementService.Application.UserAccounts;
using UserManagementService.Application.UserDirectory;

namespace UserManagementService.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IManagementService, ManagementService>();
        services.AddScoped<ISuperAdminService, SuperAdminService>();
        services.AddScoped<IUserDirectoryService, UserDirectoryService>();
        services.AddScoped<IUserStatusService, UserStatusService>();
        services.AddScoped<IClassroomAdminService, ClassroomAdminService>();
        services.AddScoped<IClassroomMemberAdminService, ClassroomMemberAdminService>();
        services.AddScoped<ISessionMonitorService, SessionMonitorService>();
        services.AddScoped<IKnowledgeAdminService, KnowledgeAdminService>();
        services.AddScoped<IOutputAdminService, OutputAdminService>();
        // AutoMapper 14 changed this overload: the assembly list moved onto the configuration
        // expression, and the (Assembly[]) form no longer exists.
        services.AddAutoMapper(cfg => cfg.AddMaps(Assembly.GetExecutingAssembly()));

        return services;
    }
}
