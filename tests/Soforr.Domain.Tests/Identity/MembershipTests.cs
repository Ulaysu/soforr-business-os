using Soforr.Domain.Common;
using Soforr.Domain.Identity;

namespace Soforr.Domain.Tests.Identity;

public class MembershipTests
{
    private static readonly Guid Id = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c1a01");
    private static readonly Guid UserId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c1a02");
    private static readonly Guid OrganizationId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c1a03");
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(Role.Owner)]
    [InlineData(Role.Manager)]
    [InlineData(Role.Staff)]
    public void Create_WithValidValues_KeepsThem(Role role)
    {
        var membership = Membership.Create(Id, UserId, OrganizationId, role, Now);

        Assert.Equal(Id, membership.Id);
        Assert.Equal(UserId, membership.UserId);
        Assert.Equal(OrganizationId, membership.OrganizationId);
        Assert.Equal(role, membership.Role);
        Assert.Equal(Now, membership.CreatedAtUtc);
    }

    [Fact]
    public void Create_WithoutAnId_IsRejected() =>
        AssertRejected("membership.id_required", () => Membership.Create(Guid.Empty, UserId, OrganizationId, Role.Owner, Now));

    [Fact]
    public void Create_WithoutAUser_IsRejected() =>
        AssertRejected("membership.user_required", () => Membership.Create(Id, Guid.Empty, OrganizationId, Role.Owner, Now));

    [Fact]
    public void Create_WithoutAnOrganization_IsRejected() =>
        AssertRejected("membership.organization_required", () => Membership.Create(Id, UserId, Guid.Empty, Role.Owner, Now));

    [Fact]
    public void Create_WithAnUnknownRole_IsRejected() =>
        AssertRejected("membership.role_invalid", () => Membership.Create(Id, UserId, OrganizationId, (Role)99, Now));

    [Fact]
    public void Create_WithANonUtcTimestamp_IsRejected() =>
        AssertRejected("membership.timestamp_not_utc", () => Membership.Create(Id, UserId, OrganizationId, Role.Owner, new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(1))));

    private static void AssertRejected(string code, Func<Membership> create)
    {
        var exception = Assert.Throws<DomainException>(create);
        Assert.Equal(code, exception.Code);
    }
}
