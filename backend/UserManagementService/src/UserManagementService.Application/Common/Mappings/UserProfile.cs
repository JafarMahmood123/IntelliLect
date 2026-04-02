using AutoMapper;
using UserManagementService.Application.DTOs.Auth;
using UserManagementService.Application.DTOs.User;
using UserManagementService.Domain.Entities;

public sealed class UserProfile : Profile
{
    public UserProfile()
    {
        // Entity -> DTO
        CreateMap<User, UserResponse>()
            .ForMember(dest => dest.RoleName, 
                opt => opt.MapFrom(src => src.Role.Name.ToString()))
            .ForMember(dest => dest.Status, 
                opt => opt.MapFrom(src => src.Status.ToString()));

        // DTO -> Entity (NEW)
        CreateMap<RegisterRequest, User>()
            .ForMember(dest => dest.Id, opt => opt.MapFrom(_ => Guid.NewGuid()))
            .ForMember(dest => dest.CreatedAtUtc, opt => opt.MapFrom(_ => DateTime.UtcNow))
            .ForMember(dest => dest.PasswordHash, opt => opt.Ignore()); // Handled by Hasher in service
    }
}