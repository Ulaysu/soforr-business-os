using System.Net;
using Microsoft.EntityFrameworkCore;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Identity;
using Soforr.Domain.Inventory;
using Soforr.Domain.Organizations;
using Soforr.Infrastructure.Persistence;
using static Soforr.Api.IntegrationTests.Identity.AuthClient;

namespace Soforr.Api.IntegrationTests.TenantIsolation;

/// <summary>
/// Cross-tenant tests for every tenant-owned entity that exists (organizations, memberships, products, stock levels and movements), through the data
/// layer and through the real application (ADR 0002): a user of one organization cannot read, change, delete or
/// create another organization's data, and an unknown or foreign id behaves exactly like a missing one. When a
/// module adds a tenant-owned entity, it adds its cases here (the convention test fails until it has a filter).
/// </summary>
[Trait("Category", "Integration")]
public sealed class TenantIsolationTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    // ---- reads: the query filter

    [Fact]
    public async Task Reads_SeeOnlyTheCurrentOrganizationsRows()
    {
        using var tenants = await TwoTenants.CreateAsync(database);

        await using var asA = database.CreateContext(tenants.OrganizationA);
        await using var asB = database.CreateContext(tenants.OrganizationB);

        Assert.Equal([tenants.OrganizationA], await asA.Set<Organization>().Select(o => o.Id).ToArrayAsync());
        Assert.Equal([tenants.OrganizationB], await asB.Set<Organization>().Select(o => o.Id).ToArrayAsync());
        Assert.Equal([tenants.UserA], await asA.Set<Membership>().Select(m => m.UserId).ToArrayAsync());
        Assert.Equal([tenants.UserB], await asB.Set<Membership>().Select(m => m.UserId).ToArrayAsync());
    }

    [Fact]
    public async Task AForeignId_BehavesLikeAMissingOne_WhateverTheQuery()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        await using var asA = database.CreateContext(tenants.OrganizationA);

        Assert.Null(await asA.Set<Organization>().SingleOrDefaultAsync(o => o.Id == tenants.OrganizationB));
        Assert.Null(await asA.Set<Organization>().FindAsync(tenants.OrganizationB));
        Assert.False(await asA.Set<Organization>().AnyAsync(o => o.Id == tenants.OrganizationB));
        Assert.Null(await asA.Set<Membership>().SingleOrDefaultAsync(m => m.OrganizationId == tenants.OrganizationB));
        Assert.Null(await asA.Set<Membership>().SingleOrDefaultAsync(m => m.UserId == tenants.UserB));
        Assert.Equal(0, await asA.Set<Membership>().CountAsync(m => m.OrganizationId == tenants.OrganizationB));
    }

    [Fact]
    public async Task WithoutAnOrganization_ReadsSeeNothing_SoTheContextFailsClosed()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        await using var nobody = database.CreateContext();

        Assert.Equal(0, await nobody.Set<Organization>().CountAsync());
        Assert.Equal(0, await nobody.Set<Membership>().CountAsync());
    }

    // ---- writes: the guard on save

    [Fact]
    public async Task ChangingAnotherOrganizationsRow_IsRefused_AndChangesNothing()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        await using (var asA = database.CreateContext(tenants.OrganizationA))
        {
            // The row can only be reached by bypassing the filter, as buggy code taking an id from the caller might.
            var theirs = await asA.Set<Organization>().IgnoreQueryFilters().SingleAsync(o => o.Id == tenants.OrganizationB);
            theirs.UpdateSettings("Hijacked", "UTC", DateTimeOffset.UtcNow);

            await Assert.ThrowsAsync<TenantIsolationException>(() => asA.SaveChangesAsync());
        }

        await using var asB = database.CreateContext(tenants.OrganizationB);
        Assert.Equal("Tenant B", (await asB.Set<Organization>().SingleAsync()).Name);
    }

    [Fact]
    public async Task DeletingAnotherOrganizationsRow_IsRefused_AndDeletesNothing()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        await using (var asA = database.CreateContext(tenants.OrganizationA))
        {
            var theirs = await asA.Set<Membership>().IgnoreQueryFilters().SingleAsync(m => m.UserId == tenants.UserB);
            asA.Set<Membership>().Remove(theirs);

            await Assert.ThrowsAsync<TenantIsolationException>(() => asA.SaveChangesAsync());
        }

        await using var asB = database.CreateContext(tenants.OrganizationB);
        Assert.Equal(1, await asB.Set<Membership>().CountAsync());
    }

    [Fact]
    public async Task CreatingARowInAnotherOrganization_IsRefused()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        await using var asA = database.CreateContext(tenants.OrganizationA);
        var injected = Membership.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), tenants.OrganizationB, Role.Owner, DateTimeOffset.UtcNow);
        asA.Set<Membership>().Add(injected);

        await Assert.ThrowsAsync<TenantIsolationException>(() => asA.SaveChangesAsync());

        await using var asB = database.CreateContext(tenants.OrganizationB);
        Assert.Equal(1, await asB.Set<Membership>().CountAsync());
    }

    [Fact]
    public async Task ChangingTheOwnRow_IsAllowed()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        await using var asA = database.CreateContext(tenants.OrganizationA);
        var mine = await asA.Set<Organization>().SingleAsync();
        mine.UpdateSettings("Renamed A", "UTC", DateTimeOffset.UtcNow);

        await asA.SaveChangesAsync();

        await using var verify = database.CreateContext(tenants.OrganizationA);
        Assert.Equal("Renamed A", (await verify.Set<Organization>().SingleAsync()).Name);
    }

    [Fact]
    public async Task WithoutAnOrganization_ANewTenantCanBeCreated_WhichIsHowRegistrationWorks()
    {
        var id = Guid.CreateVersion7();
        await using (var nobody = database.CreateContext())
        {
            nobody.Set<Organization>().Add(Organization.Create(id, "Brand new", Currency.Create("GMD"), "UTC", DateTimeOffset.UtcNow));
            await nobody.SaveChangesAsync();
        }

        await using var asNew = database.CreateContext(id);
        Assert.Equal("Brand new", (await asNew.Set<Organization>().SingleAsync()).Name);
    }

    // ---- products

    private async Task<(Guid A, Guid B)> SeedProductsAsync(TwoTenants tenants)
    {
        var a = NewProduct(tenants.OrganizationA, "SHARED-SKU");
        var b = NewProduct(tenants.OrganizationB, "shared-sku");
        await using (var asA = database.CreateContext(tenants.OrganizationA))
        {
            asA.Set<Product>().Add(a);
            await asA.SaveChangesAsync();
        }

        await using (var asB = database.CreateContext(tenants.OrganizationB))
        {
            asB.Set<Product>().Add(b);
            await asB.SaveChangesAsync();
        }

        return (a.Id, b.Id);
    }

    private static Product NewProduct(Guid organizationId, string sku)
    {
        var currency = Currency.Create("GMD");
        return Product.Create(Guid.CreateVersion7(), organizationId, sku, "Rice", "bag", Money.Create(10m, currency), Money.Create(5m, currency), null, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Products_AreReadOnlyWithinTheCurrentOrganization_AndAForeignIdOrSkuIsMissing()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        var (idA, idB) = await SeedProductsAsync(tenants);
        await using var asA = database.CreateContext(tenants.OrganizationA);
        await using var nobody = database.CreateContext();

        Assert.Equal([idA], await asA.Set<Product>().Select(p => p.Id).ToArrayAsync());
        Assert.Null(await asA.Set<Product>().SingleOrDefaultAsync(p => p.Id == idB));
        Assert.Null(await asA.Set<Product>().FindAsync(idB));
        Assert.False(await asA.Set<Product>().AnyAsync(p => p.OrganizationId == tenants.OrganizationB));
        Assert.Equal(0, await nobody.Set<Product>().CountAsync());
    }

    [Fact]
    public async Task ChangingOrDeletingAnotherOrganizationsProduct_IsRefused_AndChangesNothing()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        var (_, idB) = await SeedProductsAsync(tenants);

        await using (var change = database.CreateContext(tenants.OrganizationA))
        {
            var theirs = await change.Set<Product>().IgnoreQueryFilters().SingleAsync(p => p.Id == idB);
            theirs.Deactivate(DateTimeOffset.UtcNow);
            await Assert.ThrowsAsync<TenantIsolationException>(() => change.SaveChangesAsync());
        }

        await using (var delete = database.CreateContext(tenants.OrganizationA))
        {
            delete.Set<Product>().Remove(await delete.Set<Product>().IgnoreQueryFilters().SingleAsync(p => p.Id == idB));
            await Assert.ThrowsAsync<TenantIsolationException>(() => delete.SaveChangesAsync());
        }

        await using var asB = database.CreateContext(tenants.OrganizationB);
        Assert.True((await asB.Set<Product>().SingleAsync(p => p.Id == idB)).IsActive);
    }

    [Fact]
    public async Task CreatingAProductInAnotherOrganization_IsRefused()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        await using var asA = database.CreateContext(tenants.OrganizationA);
        asA.Set<Product>().Add(NewProduct(tenants.OrganizationB, "INJECTED"));

        await Assert.ThrowsAsync<TenantIsolationException>(() => asA.SaveChangesAsync());

        await using var asB = database.CreateContext(tenants.OrganizationB);
        Assert.Equal(0, await asB.Set<Product>().CountAsync());
    }

    // ---- stock levels and movements

    private async Task<(Guid ProductA, Guid ProductB, Guid MovementB)> SeedStockAsync(TwoTenants tenants)
    {
        var productA = NewProduct(tenants.OrganizationA, "STOCK-A");
        var productB = NewProduct(tenants.OrganizationB, "STOCK-B");
        Guid movementB = Guid.Empty;
        foreach (var (organization, product) in new[] { (tenants.OrganizationA, productA), (tenants.OrganizationB, productB) })
        {
            var movement = InventoryMovement.Record(Guid.CreateVersion7(), organization, product.Id, QuantityDelta.Create(5m), InventoryMovementReason.PurchaseReceived, InventorySourceType.PurchaseReceipt, Guid.CreateVersion7(), Guid.CreateVersion7(), null, DateTimeOffset.UtcNow);
            var level = StockLevel.Open(organization, product.Id, DateTimeOffset.UtcNow);
            level.Apply(movement);
            await using var db = database.CreateContext(organization);
            db.Set<Product>().Add(product);
            await db.SaveChangesAsync();
            db.Set<InventoryMovement>().Add(movement);
            db.Set<StockLevel>().Add(level);
            await db.SaveChangesAsync();
            if (organization == tenants.OrganizationB)
            {
                movementB = movement.Id;
            }
        }

        return (productA.Id, productB.Id, movementB);
    }

    [Fact]
    public async Task StockLevelsAndMovements_AreReadOnlyWithinTheCurrentOrganization()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        var (productA, productB, movementB) = await SeedStockAsync(tenants);
        await using var asA = database.CreateContext(tenants.OrganizationA);
        await using var nobody = database.CreateContext();

        Assert.Equal([productA], await asA.Set<StockLevel>().Select(l => l.ProductId).ToArrayAsync());
        Assert.Equal(1, await asA.Set<InventoryMovement>().CountAsync());
        Assert.Null(await asA.Set<StockLevel>().SingleOrDefaultAsync(l => l.ProductId == productB));
        Assert.Null(await asA.Set<InventoryMovement>().SingleOrDefaultAsync(m => m.Id == movementB));
        Assert.Equal(0, await nobody.Set<StockLevel>().CountAsync());
        Assert.Equal(0, await nobody.Set<InventoryMovement>().CountAsync());
    }

    [Fact]
    public async Task ChangingAnotherOrganizationsStockLevel_IsRefused_AndChangesNothing()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        var (_, productB, _) = await SeedStockAsync(tenants);

        await using (var asA = database.CreateContext(tenants.OrganizationA))
        {
            var theirs = await asA.Set<StockLevel>().IgnoreQueryFilters().SingleAsync(l => l.ProductId == productB);
            theirs.Apply(InventoryMovement.Record(Guid.CreateVersion7(), tenants.OrganizationB, productB, QuantityDelta.Create(-1m), InventoryMovementReason.SaleCompleted, InventorySourceType.Sale, Guid.CreateVersion7(), Guid.CreateVersion7(), null, DateTimeOffset.UtcNow));
            await Assert.ThrowsAsync<TenantIsolationException>(() => asA.SaveChangesAsync());
        }

        await using var asB = database.CreateContext(tenants.OrganizationB);
        Assert.Equal(5m, (await asB.Set<StockLevel>().SingleAsync()).Quantity.Value);
    }

    [Fact]
    public async Task CreatingAMovementOrALevelInAnotherOrganization_IsRefused()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        var (_, productB, _) = await SeedStockAsync(tenants);
        await using var asA = database.CreateContext(tenants.OrganizationA);
        asA.Set<InventoryMovement>().Add(InventoryMovement.Record(Guid.CreateVersion7(), tenants.OrganizationB, productB, QuantityDelta.Create(9m), InventoryMovementReason.PurchaseReceived, InventorySourceType.PurchaseReceipt, Guid.CreateVersion7(), Guid.CreateVersion7(), null, DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<TenantIsolationException>(() => asA.SaveChangesAsync());

        await using var asB = database.CreateContext(tenants.OrganizationB);
        Assert.Equal(1, await asB.Set<InventoryMovement>().CountAsync());
    }

    // ---- through the real application

    [Fact]
    public async Task OverHttp_TenantACannotReadTenantBsOrganization_AndGetsTheSameAnswerAsForAMissingOne()
    {
        using var tenants = await TwoTenants.CreateAsync(database);

        var foreign = await tenants.ClientA.GetAsync(Relative($"/api/v1/organizations/{tenants.OrganizationB}"));
        var missing = await tenants.ClientA.GetAsync(Relative($"/api/v1/organizations/{Guid.CreateVersion7()}"));

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("organization.not_found", (await ReadAsync(foreign)).GetProperty("code").GetString());
        Assert.Equal("organization.not_found", (await ReadAsync(missing)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task OverHttp_TenantACannotChangeTenantBsOrganization_EvenWithItsIdInTheRouteAndTheBody()
    {
        using var tenants = await TwoTenants.CreateAsync(database);

        var viaRoute = await SendAsync(tenants.ClientA, HttpMethod.Put, $"/api/v1/organizations/{tenants.OrganizationB}/settings", new { name = "Hijacked", timeZoneId = "UTC" });
        var viaBody = await SendAsync(
            tenants.ClientA,
            HttpMethod.Put,
            $"/api/v1/organizations/{tenants.OrganizationA}/settings",
            new { name = "Renamed A", timeZoneId = "UTC", organizationId = tenants.OrganizationB, id = tenants.OrganizationB });

        Assert.Equal(HttpStatusCode.NotFound, viaRoute.StatusCode);
        Assert.Equal(HttpStatusCode.OK, viaBody.StatusCode);
        var b = await ReadAsync(await tenants.ClientB.GetAsync(Relative($"/api/v1/organizations/{tenants.OrganizationB}")));
        Assert.Equal("Tenant B", b.GetProperty("name").GetString());
        var a = await ReadAsync(await tenants.ClientA.GetAsync(Relative($"/api/v1/organizations/{tenants.OrganizationA}")));
        Assert.Equal("Renamed A", a.GetProperty("name").GetString());
    }

    [Fact]
    public async Task EachTenant_SeesItsOwnOrganizationAndRole_FromTheSession()
    {
        using var tenants = await TwoTenants.CreateAsync(database);

        var a = await ReadAsync(await tenants.ClientA.GetAsync(Relative("/api/v1/auth/me")));
        var b = await ReadAsync(await tenants.ClientB.GetAsync(Relative("/api/v1/auth/me")));

        Assert.Equal(tenants.OrganizationA, a.GetProperty("organizationId").GetGuid());
        Assert.Equal(tenants.OrganizationB, b.GetProperty("organizationId").GetGuid());
    }

    [Fact]
    public async Task AMemberOfOrganizationA_CannotReachOrganizationB_WhateverTheirRole()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        var manager = await SignInAsMemberAsync(tenants.Factory, database, tenants.OrganizationA, Role.Manager);
        using var _ = manager;

        var get = await manager.GetAsync(Relative($"/api/v1/organizations/{tenants.OrganizationB}"));

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }
}
