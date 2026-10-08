using System.Net;
using Microsoft.EntityFrameworkCore;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Domain.Audit;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Customers;
using Soforr.Domain.Identity;
using Soforr.Domain.Inventory;
using Soforr.Domain.Organizations;
using Soforr.Domain.Purchasing;
using Soforr.Domain.Sales;
using Soforr.Infrastructure.Persistence;
using static Soforr.Api.IntegrationTests.Identity.AuthClient;

namespace Soforr.Api.IntegrationTests.TenantIsolation;

/// <summary>
/// Cross-tenant tests for every tenant-owned entity that exists (organizations, memberships, products, stock levels, movements, audit events, suppliers, purchase orders and sales), through the data
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
            theirs.UpdateSettings("Hijacked", "UTC", null, DateTimeOffset.UtcNow);

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
        mine.UpdateSettings("Renamed A", "UTC", null, DateTimeOffset.UtcNow);

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

    // ---- audit events

    private static AuditEvent NewAuditEvent(Guid organizationId, string action) =>
        AuditEvent.Record(Guid.CreateVersion7(), organizationId, Guid.CreateVersion7(), action, null, null, null, null, DateTimeOffset.UtcNow);

    [Fact]
    public async Task AuditEvents_AreReadOnlyWithinTheCurrentOrganization()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        await using (var seedA = database.CreateContext(tenants.OrganizationA))
        {
            seedA.Set<AuditEvent>().Add(NewAuditEvent(tenants.OrganizationA, "tenant.a_event"));
            await seedA.SaveChangesAsync();
        }

        await using (var seedB = database.CreateContext(tenants.OrganizationB))
        {
            seedB.Set<AuditEvent>().Add(NewAuditEvent(tenants.OrganizationB, "tenant.b_event"));
            await seedB.SaveChangesAsync();
        }

        await using var asA = database.CreateContext(tenants.OrganizationA);
        await using var nobody = database.CreateContext();

        var actionsA = await asA.Set<AuditEvent>().Select(e => e.Action).ToArrayAsync();
        Assert.Contains("tenant.a_event", actionsA);
        Assert.DoesNotContain("tenant.b_event", actionsA);
        Assert.False(await asA.Set<AuditEvent>().AnyAsync(e => e.OrganizationId == tenants.OrganizationB));
        Assert.Equal(0, await nobody.Set<AuditEvent>().CountAsync());
    }

    [Fact]
    public async Task CreatingAnAuditEventInAnotherOrganization_IsRefused()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        await using var asA = database.CreateContext(tenants.OrganizationA);
        asA.Set<AuditEvent>().Add(NewAuditEvent(tenants.OrganizationB, "tenant.injected"));

        await Assert.ThrowsAsync<TenantIsolationException>(() => asA.SaveChangesAsync());

        await using var asB = database.CreateContext(tenants.OrganizationB);
        Assert.DoesNotContain("tenant.injected", await asB.Set<AuditEvent>().Select(e => e.Action).ToArrayAsync());
    }

    // ---- suppliers and purchase orders

    private async Task<(Guid SupplierA, Guid SupplierB, Guid OrderA, Guid OrderB, Guid LineB)> SeedPurchasingAsync(TwoTenants tenants)
    {
        var ids = new List<(Guid Supplier, Guid Order, Guid Line)>();
        foreach (var organization in new[] { tenants.OrganizationA, tenants.OrganizationB })
        {
            var product = NewProduct(organization, "PO-SKU");
            var supplier = Supplier.Create(Guid.CreateVersion7(), organization, "Supplier", null, DateTimeOffset.UtcNow);
            var order = PurchaseOrder.CreateDraft(Guid.CreateVersion7(), organization, supplier.Id, Currency.Create("GMD"), null, DateTimeOffset.UtcNow);
            var line = order.AddLine(Guid.CreateVersion7(), product.Id, product.Sku, product.Name, product.UnitOfMeasure, Quantity.Create(2m), Money.Create(5m, Currency.Create("GMD")), DateTimeOffset.UtcNow);
            await using var db = database.CreateContext(organization);
            db.Set<Product>().Add(product);
            db.Set<Supplier>().Add(supplier);
            await db.SaveChangesAsync();
            db.Set<PurchaseOrder>().Add(order);
            await db.SaveChangesAsync();
            ids.Add((supplier.Id, order.Id, line.Id));
        }

        return (ids[0].Supplier, ids[1].Supplier, ids[0].Order, ids[1].Order, ids[1].Line);
    }

    [Fact]
    public async Task SuppliersOrdersAndLines_AreReadOnlyWithinTheCurrentOrganization()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        var (supplierA, supplierB, orderA, orderB, lineB) = await SeedPurchasingAsync(tenants);
        await using var asA = database.CreateContext(tenants.OrganizationA);
        await using var nobody = database.CreateContext();

        Assert.Equal([supplierA], await asA.Set<Supplier>().Select(s => s.Id).ToArrayAsync());
        Assert.Equal([orderA], await asA.Set<PurchaseOrder>().Select(o => o.Id).ToArrayAsync());
        Assert.Null(await asA.Set<Supplier>().SingleOrDefaultAsync(s => s.Id == supplierB));
        Assert.Null(await asA.Set<PurchaseOrder>().FindAsync(orderB));
        Assert.False(await asA.Set<PurchaseOrderLine>().AnyAsync(l => l.Id == lineB));
        Assert.Equal(0, await nobody.Set<Supplier>().CountAsync());
        Assert.Equal(0, await nobody.Set<PurchaseOrder>().CountAsync());
        Assert.Equal(0, await nobody.Set<PurchaseOrderLine>().CountAsync());
    }

    [Fact]
    public async Task ChangingOrDeletingAnotherOrganizationsSupplierOrOrder_IsRefused_AndChangesNothing()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        var (_, supplierB, _, orderB, _) = await SeedPurchasingAsync(tenants);

        await using (var changeSupplier = database.CreateContext(tenants.OrganizationA))
        {
            var theirs = await changeSupplier.Set<Supplier>().IgnoreQueryFilters().SingleAsync(s => s.Id == supplierB);
            theirs.Deactivate(DateTimeOffset.UtcNow);
            await Assert.ThrowsAsync<TenantIsolationException>(() => changeSupplier.SaveChangesAsync());
        }

        await using (var changeOrder = database.CreateContext(tenants.OrganizationA))
        {
            var theirs = await changeOrder.Set<PurchaseOrder>().IgnoreQueryFilters().Include(o => o.Lines).SingleAsync(o => o.Id == orderB);
            theirs.Place(DateTimeOffset.UtcNow);
            await Assert.ThrowsAsync<TenantIsolationException>(() => changeOrder.SaveChangesAsync());
        }

        await using (var delete = database.CreateContext(tenants.OrganizationA))
        {
            delete.Set<PurchaseOrder>().Remove(await delete.Set<PurchaseOrder>().IgnoreQueryFilters().SingleAsync(o => o.Id == orderB));
            await Assert.ThrowsAsync<TenantIsolationException>(() => delete.SaveChangesAsync());
        }

        await using var asB = database.CreateContext(tenants.OrganizationB);
        Assert.True((await asB.Set<Supplier>().SingleAsync(s => s.Id == supplierB)).IsActive);
        Assert.Equal(PurchaseOrderStatus.Draft, (await asB.Set<PurchaseOrder>().SingleAsync(o => o.Id == orderB)).Status);
    }

    [Fact]
    public async Task CreatingASupplierOrAnOrderInAnotherOrganization_IsRefused()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        var currency = Currency.Create("GMD");
        await using (var asA = database.CreateContext(tenants.OrganizationA))
        {
            asA.Set<Supplier>().Add(Supplier.Create(Guid.CreateVersion7(), tenants.OrganizationB, "Injected", null, DateTimeOffset.UtcNow));
            await Assert.ThrowsAsync<TenantIsolationException>(() => asA.SaveChangesAsync());
        }

        await using (var asA = database.CreateContext(tenants.OrganizationA))
        {
            asA.Set<PurchaseOrder>().Add(PurchaseOrder.CreateDraft(Guid.CreateVersion7(), tenants.OrganizationB, Guid.CreateVersion7(), currency, null, DateTimeOffset.UtcNow));
            await Assert.ThrowsAsync<TenantIsolationException>(() => asA.SaveChangesAsync());
        }

        await using var asB = database.CreateContext(tenants.OrganizationB);
        Assert.Equal(0, await asB.Set<Supplier>().CountAsync());
        Assert.Equal(0, await asB.Set<PurchaseOrder>().CountAsync());
    }

    // ---- customers

    private async Task<(Guid CustomerA, Guid CustomerB)> SeedCustomersAsync(TwoTenants tenants)
    {
        var ids = new List<Guid>();
        foreach (var organization in new[] { tenants.OrganizationA, tenants.OrganizationB })
        {
            var customer = Customer.Create(Guid.CreateVersion7(), organization, "Customer", "+220 555 0100", 30, DateTimeOffset.UtcNow);
            await using var db = database.CreateContext(organization);
            db.Set<Customer>().Add(customer);
            await db.SaveChangesAsync();
            ids.Add(customer.Id);
        }

        return (ids[0], ids[1]);
    }

    [Fact]
    public async Task Customers_AreReadOnlyWithinTheCurrentOrganization()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        var (customerA, customerB) = await SeedCustomersAsync(tenants);
        await using var asA = database.CreateContext(tenants.OrganizationA);
        await using var nobody = database.CreateContext();

        Assert.Equal([customerA], await asA.Set<Customer>().Select(c => c.Id).ToArrayAsync());
        Assert.Null(await asA.Set<Customer>().FindAsync(customerB));
        Assert.Equal(0, await nobody.Set<Customer>().CountAsync());
    }

    [Fact]
    public async Task ChangingOrDeletingAnotherOrganizationsCustomer_IsRefused_AndChangesNothing()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        var (_, customerB) = await SeedCustomersAsync(tenants);

        await using (var update = database.CreateContext(tenants.OrganizationA))
        {
            var theirs = await update.Set<Customer>().IgnoreQueryFilters().SingleAsync(c => c.Id == customerB);
            theirs.Update("Injected", null, null, DateTimeOffset.UtcNow);
            await Assert.ThrowsAsync<TenantIsolationException>(() => update.SaveChangesAsync());
        }

        await using (var delete = database.CreateContext(tenants.OrganizationA))
        {
            delete.Set<Customer>().Remove(await delete.Set<Customer>().IgnoreQueryFilters().SingleAsync(c => c.Id == customerB));
            await Assert.ThrowsAsync<TenantIsolationException>(() => delete.SaveChangesAsync());
        }

        await using var asB = database.CreateContext(tenants.OrganizationB);
        Assert.Equal("Customer", (await asB.Set<Customer>().SingleAsync(c => c.Id == customerB)).Name);
    }

    [Fact]
    public async Task CreatingACustomerInAnotherOrganization_IsRefused()
    {
        using var tenants = await TwoTenants.CreateAsync(database);

        await using (var asA = database.CreateContext(tenants.OrganizationA))
        {
            asA.Set<Customer>().Add(Customer.Create(Guid.CreateVersion7(), tenants.OrganizationB, "Injected", null, null, DateTimeOffset.UtcNow));
            await Assert.ThrowsAsync<TenantIsolationException>(() => asA.SaveChangesAsync());
        }

        await using var asB = database.CreateContext(tenants.OrganizationB);
        Assert.Equal(0, await asB.Set<Customer>().CountAsync());
    }

    // ---- sales

    private async Task<(Guid SaleA, Guid SaleB, Guid LineB)> SeedSalesAsync(TwoTenants tenants)
    {
        var ids = new List<(Guid Sale, Guid Line)>();
        foreach (var organization in new[] { tenants.OrganizationA, tenants.OrganizationB })
        {
            var product = NewProduct(organization, "SALE-SKU");
            var currency = Currency.Create("GMD");
            var line = new SaleLineInput(Guid.CreateVersion7(), product.Id, product.Sku, product.Name, product.UnitOfMeasure, Quantity.Create(2m), Money.Create(5m, currency), Money.Create(5m, currency), Money.Create(3m, currency), null);
            var sale = Sale.Complete(Guid.CreateVersion7(), organization, currency, Guid.CreateVersion7(), DateTimeOffset.UtcNow, [line]);
            await using var db = database.CreateContext(organization);
            db.Set<Product>().Add(product);
            await db.SaveChangesAsync();
            db.Set<Sale>().Add(sale);
            await db.SaveChangesAsync();
            ids.Add((sale.Id, line.LineId));
        }

        return (ids[0].Sale, ids[1].Sale, ids[1].Line);
    }

    [Fact]
    public async Task SalesAndSaleLines_AreReadOnlyWithinTheCurrentOrganization()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        var (saleA, saleB, lineB) = await SeedSalesAsync(tenants);
        await using var asA = database.CreateContext(tenants.OrganizationA);
        await using var nobody = database.CreateContext();

        Assert.Equal([saleA], await asA.Set<Sale>().Select(s => s.Id).ToArrayAsync());
        Assert.Null(await asA.Set<Sale>().SingleOrDefaultAsync(s => s.Id == saleB));
        Assert.False(await asA.Set<SaleLine>().AnyAsync(l => l.Id == lineB));
        Assert.Equal(0, await nobody.Set<Sale>().CountAsync());
        Assert.Equal(0, await nobody.Set<SaleLine>().CountAsync());
    }

    [Fact]
    public async Task VoidingOrDeletingAnotherOrganizationsSale_IsRefused_AndChangesNothing()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        var (_, saleB, _) = await SeedSalesAsync(tenants);

        await using (var voiding = database.CreateContext(tenants.OrganizationA))
        {
            var theirs = await voiding.Set<Sale>().IgnoreQueryFilters().Include(s => s.Lines).SingleAsync(s => s.Id == saleB);
            theirs.Void(Guid.CreateVersion7(), "Injected", DateTimeOffset.UtcNow);
            await Assert.ThrowsAsync<TenantIsolationException>(() => voiding.SaveChangesAsync());
        }

        await using (var delete = database.CreateContext(tenants.OrganizationA))
        {
            delete.Set<Sale>().Remove(await delete.Set<Sale>().IgnoreQueryFilters().SingleAsync(s => s.Id == saleB));
            await Assert.ThrowsAsync<TenantIsolationException>(() => delete.SaveChangesAsync());
        }

        await using var asB = database.CreateContext(tenants.OrganizationB);
        Assert.Equal(SaleStatus.Completed, (await asB.Set<Sale>().SingleAsync(s => s.Id == saleB)).Status);
    }

    [Fact]
    public async Task CreatingASaleInAnotherOrganization_IsRefused()
    {
        using var tenants = await TwoTenants.CreateAsync(database);
        var currency = Currency.Create("GMD");
        var line = new SaleLineInput(Guid.CreateVersion7(), Guid.CreateVersion7(), "S", "N", "u", Quantity.Create(1m), Money.Create(1m, currency), Money.Create(1m, currency), Money.Create(1m, currency), null);

        await using (var asA = database.CreateContext(tenants.OrganizationA))
        {
            asA.Set<Sale>().Add(Sale.Complete(Guid.CreateVersion7(), tenants.OrganizationB, currency, Guid.CreateVersion7(), DateTimeOffset.UtcNow, [line]));
            await Assert.ThrowsAsync<TenantIsolationException>(() => asA.SaveChangesAsync());
        }

        await using var asB = database.CreateContext(tenants.OrganizationB);
        Assert.Equal(0, await asB.Set<Sale>().CountAsync());
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
