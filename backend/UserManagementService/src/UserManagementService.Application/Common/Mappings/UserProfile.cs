using AutoMapper;
using UserManagementService.Application.DTOs;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Application.Common.Mappings;

public sealed class UserProfile : Profile
{
    public UserProfile()
    {
        // Entity -> DTO
        CreateMap<User, UserResponse>()
            .ForMember(dest => dest.RoleName, 
                opt => opt.MapFrom(src => src.Role.Name.ToString()));

        // DTO -> Entity (Optional, usually we use factory methods for creation)
        // This is useful if you want to automate the RegisterRequest -> User mapping
        CreateMap<RegisterRequest, User>();
    }
}