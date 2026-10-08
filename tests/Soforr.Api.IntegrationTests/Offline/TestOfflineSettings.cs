using Soforr.Application.Offline;
using Soforr.Domain.Offline;

namespace Soforr.Api.IntegrationTests.Offline;

/// <summary>The offline settings with the documented defaults (72 hour window, 10 minute minimum gap, 2 minute skew), for handler tests that build handlers by hand.</summary>
internal sealed class TestOfflineSettings : IOfflineSettings
{
    public static TestOfflineSettings Default { get; } = new();

    public OfflinePolicy Policy { get; } = new(TimeSpan.FromHours(72), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2));

    public TimeSpan GrantMaxAge { get; } = TimeSpan.FromDays(30);

    public TimeSpan EvidenceRetention { get; } = TimeSpan.FromDays(90);

    public TimeSpan ContactInterval { get; } = TimeSpan.FromMinutes(5);
}
