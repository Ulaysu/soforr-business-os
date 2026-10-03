using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Domain.Identity;

namespace Soforr.Application.Tests.Identity;

public class AuthorizationTests
{
    private sealed class FakeUser(Guid? userId, Guid? organizationId, Role? role) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;

        public Guid? OrganizationId { get; } = organizationId;

        public Role? Role { get; } = role;
    }

    private static readonly Guid Someone = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c3a01");
    private static readonly Guid SomeOrganization = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c3a02");

    // The documented matrix (docs/security/security.md). Anything not granted here must be denied.
    public static TheoryData<Role, Permission, bool> Matrix() => new()
    {
        { Role.Owner, Permission.OrganizationView, true },
        { Role.Owner, Permission.OrganizationManageSettings, true },
        { Role.Manager, Permission.OrganizationView, true },
        { Role.Manager, Permission.OrganizationManageSettings, false },
        { Role.Staff, Permission.OrganizationView, true },
        { Role.Staff, Permission.OrganizationManageSettings, false },
        { Role.Owner, Permission.ProductView, true },
        { Role.Owner, Permission.ProductViewCost, true },
        { Role.Owner, Permission.ProductManage, true },
        { Role.Manager, Permission.ProductView, true },
        { Role.Manager, Permission.ProductViewCost, true },
        { Role.Manager, Permission.ProductManage, true },
        { Role.Staff, Permission.ProductView, true },
        { Role.Staff, Permission.ProductViewCost, false },
        { Role.Staff, Permission.ProductManage, false },
        { Role.Owner, Permission.AuditView, true },
        { Role.Manager, Permission.AuditView, false },
        { Role.Staff, Permission.AuditView, false },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void ARole_HasExactlyTheDocumentedPermissions(Role role, Permission permission, bool expected) =>
        Assert.Equal(expected, RolePermissions.IsGranted(role, permission));

    [Fact]
    public void TheMatrix_CoversEveryRoleAndEveryPermission_SoANewOneCannotBeLeftUndecided()
    {
        var covered = Matrix().Select(row => ((Role)row[0], (Permission)row[1])).ToHashSet();

        var all = Enum.GetValues<Role>().SelectMany(role => Enum.GetValues<Permission>().Select(permission => (role, permission)));

        foreach (var pair in all)
        {
            Assert.Contains(pair, covered);
        }
    }

    [Fact]
    public void AnUnknownRole_HasNoPermission() =>
        Assert.All(Enum.GetValues<Permission>(), permission => Assert.False(RolePermissions.IsGranted((Role)99, permission)));

    [Fact]
    public void Has_IsFalseWithoutAMembership_AndTrueOnlyForAGrantedPermission()
    {
        Assert.False(new FakeUser(null, null, null).Has(Permission.ProductView));
        Assert.False(new FakeUser(Someone, null, Role.Owner).Has(Permission.ProductView));
        Assert.False(new FakeUser(Someone, SomeOrganization, Role.Staff).Has(Permission.ProductViewCost));
        Assert.True(new FakeUser(Someone, SomeOrganization, Role.Manager).Has(Permission.ProductViewCost));
    }

    [Fact]
    public void Require_WhenNotSignedIn_IsNotAuthenticated()
    {
        var error = new FakeUser(null, null, null).Require(Permission.OrganizationView);

        Assert.Equal("identity.not_authenticated", error!.Code);
        Assert.Equal(ErrorKind.Unauthenticated, error.Kind);
    }

    [Fact]
    public void Require_WhenSignedInWithoutAMembership_IsForbidden()
    {
        Assert.Equal("identity.forbidden", new FakeUser(Someone, null, null).Require(Permission.OrganizationView)!.Code);
        Assert.Equal("identity.forbidden", new FakeUser(Someone, SomeOrganization, null).Require(Permission.OrganizationView)!.Code);
        Assert.Equal("identity.forbidden", new FakeUser(Someone, null, Role.Owner).Require(Permission.OrganizationView)!.Code);
    }

    [Fact]
    public void Require_WithoutThePermission_IsForbidden()
    {
        var error = new FakeUser(Someone, SomeOrganization, Role.Staff).Require(Permission.OrganizationManageSettings);

        Assert.Equal("identity.forbidden", error!.Code);
        Assert.Equal(ErrorKind.Forbidden, error.Kind);
    }

    [Fact]
    public void Require_WithThePermission_ReturnsNoError() =>
        Assert.Null(new FakeUser(Someone, SomeOrganization, Role.Owner).Require(Permission.OrganizationManageSettings));
}
