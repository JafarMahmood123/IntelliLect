using AutoMapper;
using Microsoft.Extensions.Logging.Abstractions;

namespace UserManagementService.UnitTests;

/// <summary>
/// The real AutoMapper, built from the production profile — no mocking, matching
/// ClassroomService's <c>TestMapper</c>.
///
/// It exists as one helper rather than five identical constructions because AutoMapper 14 added a
/// required <c>ILoggerFactory</c> to <see cref="MapperConfiguration"/>, and five copies of that
/// call meant five places to edit for an API change that has nothing to do with any of them.
/// </summary>
public static class TestMapper
{
    /// <summary>Null-logging: these tests assert on mappings, not on AutoMapper's diagnostics.</summary>
    public static IMapper Create()
        => new MapperConfiguration(
            cfg => cfg.AddProfile<UserProfile>(), NullLoggerFactory.Instance).CreateMapper();
}
