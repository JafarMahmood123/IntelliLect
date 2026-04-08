using AutoMapper;
using ClassroomService.Application.DTOs.Classroom;
using ClassroomService.Application.DTOs.File;
using ClassroomService.Application.DTOs.Membership;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Common.Mappings;

public class ClassroomProfile : Profile
{
    public ClassroomProfile()
    {
        CreateMap<CreateClassroomRequest, Classroom>()
            .ForMember(dest => dest.Id, opt => opt.MapFrom(_ => Guid.NewGuid()))
            .ForMember(dest => dest.CreatedAtUtc, opt => opt.MapFrom(_ => DateTime.UtcNow));

        CreateMap<Classroom, ClassroomResponse>()
            .ForMember(dest => dest.FileCount, opt => opt.MapFrom(src => src.Files.Count))
            .ForMember(dest => dest.StudentCount, opt => opt.MapFrom(src => src.Memberships.Count));

        CreateMap<ClassroomFile, ClassroomFileResponse>();

        CreateMap<ClassroomMembership, MemberResponse>()
            .ForMember(dest => dest.FullName, opt => opt.Ignore());
    }
}