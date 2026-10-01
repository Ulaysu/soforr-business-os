using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Soforr.Application.Identity;
using Soforr.Application.Organizations;
using Soforr.Domain.Identity;

namespace Soforr.Api.IntegrationTests;

/// <summary>
/// The organization context and the current user are derived from the authenticated principal on the server and
/// fail closed when it is missing or unusable (ADR 0002). No database is needed.
/// </summary>
public class OrganizationContextTests
{
    private const string OrganizationClaim = "soforr.organization";
    private static readonly Guid UserId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c2a01");
    private static readonly Guid OrganizationId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c2a02");

    [Fact]
    public void WithoutAnyRequest_TheOrganizationContextHasNoOrganization_SoConsumersFailClosed()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var scope = factory.Services.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<IOrganizationContext>();
        var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();

        Assert.Null(context.OrganizationId);
        Assert.Null(user.UserId);
        Assert.Null(user.OrganizationId);
        Assert.Null(user.Role);
    }

    [Fact]
    public void ForAnAnonymousRequest_EverythingIsNull()
    {
        using var scope = ScopeFor(new ClaimsPrincipal(new ClaimsIdentity()), out var factory);
        using var _ = factory;

        Assert.Null(scope.ServiceProvider.GetRequiredService<IOrganizationContext>().OrganizationId);
        Assert.Null(scope.ServiceProvider.GetRequiredService<ICurrentUser>().UserId);
    }

    [Fact]
    public void ForAnAuthenticatedUser_TheContextComesFromTheClaims()
    {
        var principal = Authenticated(
            new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
            new Claim(OrganizationClaim, OrganizationId.ToString()),
            new Claim(ClaimTypes.Role, "Manager"));
        using var scope = ScopeFor(principal, out var factory);
        using var _ = factory;

        var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();

        Assert.Equal(UserId, user.UserId);
        Assert.Equal(OrganizationId, user.OrganizationId);
        Assert.Equal(Role.Manager, user.Role);
        Assert.Equal(OrganizationId, scope.ServiceProvider.GetRequiredService<IOrganizationContext>().OrganizationId);
    }

    [Fact]
    public void AnAuthenticatedUserWithoutAMembership_HasNoOrganization()
    {
        using var scope = ScopeFor(Authenticated(new Claim(ClaimTypes.NameIdentifier, UserId.ToString())), out var factory);
        using var _ = factory;

        Assert.Equal(UserId, scope.ServiceProvider.GetRequiredService<ICurrentUser>().UserId);
        Assert.Null(scope.ServiceProvider.GetRequiredService<IOrganizationContext>().OrganizationId);
    }

    [Theory]
    [InlineData("not-a-guid", "Owner", false, true)]
    [InlineData("00000000-0000-0000-0000-000000000000", "Owner", false, true)]
    [InlineData("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c2a02", "Superuser", true, false)]
    [InlineData("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c2a02", "owner", true, false)]
    [InlineData("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c2a02", "99", true, false)]
    public void UnusableClaims_FailClosed(string organization, string role, bool organizationUsable, bool roleUsable)
    {
        var principal = Authenticated(
            new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
            new Claim(OrganizationClaim, organization),
            new Claim(ClaimTypes.Role, role));
        using var scope = ScopeFor(principal, out var factory);
        using var _ = factory;

        var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();

        Assert.Equal(organizationUsable ? OrganizationId : null, user.OrganizationId);
        Assert.Equal(roleUsable ? Role.Owner : null, user.Role);
    }

    private static ClaimsPrincipal Authenticated(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "test"));

    private static IServiceScope ScopeFor(ClaimsPrincipal principal, out ApiFactory factory)
    {
        factory = new ApiFactory();
        var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext { User = principal };
        return scope;
    }
}
