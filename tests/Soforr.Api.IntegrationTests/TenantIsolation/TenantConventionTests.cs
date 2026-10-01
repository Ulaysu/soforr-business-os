using Microsoft.EntityFrameworkCore;
using Soforr.Application.Organizations;
using Soforr.Domain.Organizations;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Api.IntegrationTests.TenantIsolation;

/// <summary>
/// A tenant-owned entity without a query filter would leak data across organizations, so this reads the real EF model
/// and fails when one has none (ADR 0002). It needs no database: the model is built without connecting.
/// </summary>
public class TenantConventionTests
{
    private sealed class NoOrganization : IOrganizationContext
    {
        public Guid? OrganizationId => null;
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=model-only").Options, new NoOrganization());

    // Entities that are owned by a user or by Identity rather than by an organization. Adding to this list is a
    // security decision for review.
    private static readonly string[] NotTenantOwned = ["ApplicationUser", "IdentityUserClaim<Guid>", "IdentityUserLogin<Guid>", "IdentityUserToken<Guid>"];

    [Fact]
    public void EveryEntityWithAnOrganizationId_HasATenantQueryFilter()
    {
        using var context = CreateContext();

        var unfiltered = context.Model.GetEntityTypes()
            .Where(entity => entity.FindProperty("OrganizationId") is not null)
            .Where(entity => entity.GetDeclaredQueryFilters().Count == 0 && entity.BaseType is null)
            .Select(entity => entity.ClrType.Name)
            .ToArray();

        Assert.Empty(unfiltered);
    }

    [Fact]
    public void TheOrganizationItself_HasATenantQueryFilter()
    {
        using var context = CreateContext();

        Assert.NotEmpty(context.Model.FindEntityType(typeof(Organization))!.GetDeclaredQueryFilters());
    }

    [Fact]
    public void EveryEntity_IsEitherTenantFiltered_OrExplicitlyListedAsNotTenantOwned()
    {
        using var context = CreateContext();

        var undecided = context.Model.GetEntityTypes()
            .Where(entity => entity.GetDeclaredQueryFilters().Count == 0)
            .Select(entity => entity.ClrType.Name == "IdentityUserClaim`1" ? "IdentityUserClaim<Guid>" : entity.DisplayName())
            .Where(name => !NotTenantOwned.Contains(name, StringComparer.Ordinal))
            .ToArray();

        Assert.Empty(undecided);
    }
}
