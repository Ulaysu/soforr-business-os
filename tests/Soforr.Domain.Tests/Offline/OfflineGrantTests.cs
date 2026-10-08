using Soforr.Domain.Common;
using Soforr.Domain.Offline;

namespace Soforr.Domain.Tests.Offline;

public class OfflineGrantTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid User = Guid.CreateVersion7();

    private static string CodeOf(Action action) => Assert.Throws<DomainException>(action).Code;

    [Fact]
    public void AGrant_IsIssuedForAUserAndOrganization_WithItsLifetime()
    {
        var grant = OfflineGrant.Issue(Guid.CreateVersion7(), Organization, User, Now, Now.AddDays(30));

        Assert.Equal((Organization, User, Now, Now.AddDays(30)), (grant.OrganizationId, grant.UserId, grant.IssuedAtUtc, grant.AbsoluteExpiresAtUtc));
        Assert.Null(grant.RevokedAtUtc);
        Assert.Null(grant.SupersededAtUtc);
        Assert.False(grant.IsRevoked);
    }

    [Fact]
    public void AGrant_NeedsIdentifiersUtcTimesAndALifetime()
    {
        Assert.Equal("offline_grant.id_required", CodeOf(() => OfflineGrant.Issue(Guid.Empty, Organization, User, Now, Now.AddDays(1))));
        Assert.Equal("offline_grant.owner_required", CodeOf(() => OfflineGrant.Issue(Guid.CreateVersion7(), Guid.Empty, User, Now, Now.AddDays(1))));
        Assert.Equal("offline_grant.owner_required", CodeOf(() => OfflineGrant.Issue(Guid.CreateVersion7(), Organization, Guid.Empty, Now, Now.AddDays(1))));
        Assert.Equal("offline_grant.timestamp_not_utc", CodeOf(() => OfflineGrant.Issue(Guid.CreateVersion7(), Organization, User, new DateTimeOffset(Now.DateTime, TimeSpan.FromHours(1)), Now.AddDays(1))));
        Assert.Equal("offline_grant.lifetime_invalid", CodeOf(() => OfflineGrant.Issue(Guid.CreateVersion7(), Organization, User, Now, Now)));
    }

    [Fact]
    public void SupersedingAndRevoking_RecordTheFirstTimeOnly()
    {
        var grant = OfflineGrant.Issue(Guid.CreateVersion7(), Organization, User, Now, Now.AddDays(30));

        grant.Supersede(Now.AddHours(1));
        grant.Supersede(Now.AddHours(2));
        grant.Revoke(Now.AddHours(3));
        grant.Revoke(Now.AddHours(4));

        Assert.Equal((Now.AddHours(1), Now.AddHours(3), true), (grant.SupersededAtUtc, grant.RevokedAtUtc, grant.IsRevoked));
    }

    [Fact]
    public void AContact_NeedsIdentifiersAndAUtcTime()
    {
        var contact = new OfflineContact(Guid.CreateVersion7(), Organization, Guid.CreateVersion7(), Now);

        Assert.Equal(Now, contact.ObservedAtUtc);
        Assert.Equal("offline_contact.ids_required", CodeOf(() => _ = new OfflineContact(Guid.Empty, Organization, Guid.CreateVersion7(), Now)));
        Assert.Equal("offline_contact.timestamp_not_utc", CodeOf(() => _ = new OfflineContact(Guid.CreateVersion7(), Organization, Guid.CreateVersion7(), new DateTimeOffset(Now.DateTime, TimeSpan.FromHours(2)))));
    }
}
