using AutoMapper;
using ClassroomService.Application.Common.Mappings;

namespace ClassroomService.UnitTests;

/// <summary>
/// A rule over the whole mapping profile rather than a test per map.
///
/// AutoMapper resolves everything at map time, so a broken configuration is not a compile error
/// and not a startup error — it is a 500 on whichever endpoint happens to use that map, first seen
/// by whoever opened the page. The classroom roster was exactly this: `MemberResponse` is a
/// positional record, and a `ForMember(...).Ignore()` on one of its constructor parameters made
/// AutoMapper look for a parameterless constructor it does not have. Every classroom with at least
/// one student returned 500 from GET /api/classrooms/{id}/members; an empty one succeeded, because
/// mapping an empty list never constructs anything.
///
/// This validates the configuration once, so the next map added has to be complete before it can
/// ship rather than before it is first opened.
/// </summary>
public sealed class MappingConfigurationTests
{
    [Fact]
    public void The_production_mapping_profile_is_valid()
    {
        var configuration = new MapperConfiguration(cfg => cfg.AddProfile<ClassroomProfile>());

        configuration.AssertConfigurationIsValid();
    }
}
