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
            .ForMember(dest => dest.CreatedAtUtc, opt => opt.MapFrom(_ => DateTime.UtcNow))
            // Ownership comes from the authenticated caller, never from the request body — that is
            // set by ClassroomManagementService.CreateAsync after the map. Declared explicitly
            // rather than left unmapped so that adding a TeacherId to the request DTO cannot
            // quietly start letting a teacher create a classroom owned by somebody else.
            .ForMember(dest => dest.TeacherId, opt => opt.Ignore())
            // The entity's own default (Active); a new classroom is never created mid-deletion.
            .ForMember(dest => dest.Status, opt => opt.Ignore())
            // Navigation collections — populated by EF, not by a create request.
            .ForMember(dest => dest.Files, opt => opt.Ignore())
            .ForMember(dest => dest.Memberships, opt => opt.Ignore());

        CreateMap<Classroom, ClassroomResponse>()
            .ForMember(dest => dest.FileCount, opt => opt.MapFrom(src => src.Files.Count))
            .ForMember(dest => dest.StudentCount, opt => opt.MapFrom(src => src.Memberships.Count));

        CreateMap<ClassroomFile, ClassroomFileResponse>();

        // MemberResponse is a positional record, so AutoMapper has to go through its constructor.
        // `ForMember(...).Ignore()` cannot express "leave this parameter out" — it makes AutoMapper
        // look for a parameterless constructor instead, finds none, and throws at map time. The
        // classroom roster is exactly this map, so every non-empty classroom 500'd on GET /members
        // while an empty one succeeded (nothing to construct).
        //
        // FullName is deliberately blank here: UserManagementService owns the user store and is
        // what resolves names, so this service has none to supply.
        CreateMap<ClassroomMembership, MemberResponse>()
            .ForCtorParam(nameof(MemberResponse.FullName), opt => opt.MapFrom(_ => string.Empty));
    }
}