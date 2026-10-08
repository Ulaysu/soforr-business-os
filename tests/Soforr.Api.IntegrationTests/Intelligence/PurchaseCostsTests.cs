using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Intelligence;
using Soforr.Application.Intelligence.GetInsights;
using Soforr.Application.Intelligence.GetPurchaseCosts;
using Soforr.Application.Intelligence.Rules;
using Soforr.Application.Intelligence.Sources;
using Soforr.Application.Organizations;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Identity;
using Soforr.Domain.Organizations;
using Soforr.Domain.Purchasing;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Api.IntegrationTests.Intelligence;

/// <summary>
/// The purchase cost report and insights on real PostgreSQL, with the clock fixed at 2026-06-01 10:00 UTC (so the default 30-day
/// window is 2026-05-02 to 2026-05-31). Volume is reconciled against a raw SQL sum of the stored order lines.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PurchaseCostsTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LongAgo = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Currency Gmd = Currency.Create("GMD");

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }

    private sealed class FakeUser(Guid? userId, Guid? organizationId, Role? role) : ICurrentUser, IOrganizationContext
    {
        public Guid? UserId { get; } = userId;

        public Guid? OrganizationId { get; } = organizationId;

        public Role? Role { get; } = role;

        public static FakeUser In(Guid organizationId, Role role) => new(Guid.CreateVersion7(), organizationId, role);
    }

    private async Task<Guid> NewOrganizationAsync()
    {
        var id = Guid.CreateVersion7();
        await using var db = database.CreateContext();
        db.Set<Organization>().Add(Organization.Create(id, "Soforr Trading", Gmd, "UTC", LongAgo));
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> NewProductAsync(Guid organization, string name)
    {
        var product = Product.Create(Guid.CreateVersion7(), organization, "SKU-" + Guid.NewGuid().ToString("N"), name, "bag", Money.Create(10m, Gmd), Money.Create(5m, Gmd), null, LongAgo);
        await using var db = database.CreateContext(organization);
        db.Set<Product>().Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private async Task<Guid> NewSupplierAsync(Guid organization, string name)
    {
        var supplier = Supplier.Create(Guid.CreateVersion7(), organization, name, null, LongAgo);
        await using var db = database.CreateContext(organization);
        db.Set<Supplier>().Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    private enum Outcome
    {
        Draft,
        Placed,
        Received,
        Cancelled,
    }

    /// <summary>An order for one product, placed at <paramref name="placed"/> (or left as a draft / cancelled after placing).</summary>
    private async Task OrderAsync(Guid organization, Guid supplier, Guid product, decimal quantity, decimal cost, DateTimeOffset placed, Outcome outcome = Outcome.Placed)
    {
        var order = PurchaseOrder.CreateDraft(Guid.CreateVersion7(), organization, supplier, Gmd, null, placed.AddHours(-1));
        order.AddLine(Guid.CreateVersion7(), product, "SKU", "Product", "bag", Quantity.Create(quantity), Money.Create(cost, Gmd), placed.AddMinutes(-30));
        if (outcome != Outcome.Draft)
        {
            order.Place(placed);
        }

        if (outcome == Outcome.Received)
        {
            order.Receive([new ReceiptLine(order.Lines[0].Id, Quantity.Create(quantity))], placed.AddDays(1));
        }

        if (outcome == Outcome.Cancelled)
        {
            order.Cancel(placed.AddHours(1));
        }

        await using var db = database.CreateContext(organization);
        db.Set<PurchaseOrder>().Add(order);
        await db.SaveChangesAsync();
    }

    private static DateTimeOffset Day(int month, int day) => new(2026, month, day, 9, 0, 0, TimeSpan.Zero);

    private async Task<Result<PurchaseCostsDto>> ReportAsync(Guid organization, int? days = null, int? top = null, Role role = Role.Owner)
    {
        var user = FakeUser.In(organization, role);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).Options, user);
        return await new GetPurchaseCostsHandler(db, new FixedClock(), user).HandleAsync(new GetPurchaseCostsQuery(days, top), CancellationToken.None);
    }

    private async Task<Result<InsightsDto>> InsightsAsync(Guid organization, Role role = Role.Owner)
    {
        var user = FakeUser.In(organization, role);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).Options, user);
        return await new GetInsightsHandler([new PurchaseCostInsightSource(db)], new FixedClock(), user).HandleAsync(new GetInsightsQuery(), CancellationToken.None);
    }

    // ---- the report

    [Fact]
    public async Task TheReport_ComparesEachSuppliersLatestCostWithItsPreviousPurchase_AndTheSuppliersWithEachOther()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Rice");
        var acme = await NewSupplierAsync(organization, "Acme");
        var bravo = await NewSupplierAsync(organization, "Bravo");
        await OrderAsync(organization, acme, rice, 10m, 100m, Day(4, 1));
        await OrderAsync(organization, acme, rice, 20m, 125m, Day(5, 10), Outcome.Received);
        await OrderAsync(organization, bravo, rice, 5m, 150m, Day(5, 12));

        var result = await ReportAsync(organization);

        Assert.True(result.IsSuccess, result.Error?.Code);
        var report = result.Value;
        Assert.Equal(("GMD", "UTC", 365, 10m), (report.Currency, report.TimeZoneId, report.LookbackDays, report.NotableChangeThresholdPercent));
        var product = Assert.Single(report.TopProducts);
        Assert.Equal("Rice", product.Name);
        var acmeCost = product.Suppliers.Single(s => s.SupplierName == "Acme");
        Assert.Equal((125m, 100m, 25m, true, "ok"), (acmeCost.Latest.UnitCost, acmeCost.Previous!.UnitCost, acmeCost.ChangePercent, acmeCost.Notable, acmeCost.Status));
        var bravoCost = product.Suppliers.Single(s => s.SupplierName == "Bravo");
        Assert.Equal(("insufficient_history", null), (bravoCost.Status, bravoCost.ChangePercent));
        Assert.Equal(("Acme", "Bravo", 20m, true), (product.SupplierGap!.Cheapest.SupplierName, product.SupplierGap.MostExpensive.SupplierName, product.SupplierGap.GapPercent, product.SupplierGap.Notable));
        Assert.Equal((2, 25m, 125m * 20m + 150m * 5m), (product.Volume.Orders, product.Volume.Quantity, product.Volume.Spend));
        Assert.Equal([product.ProductId], report.NotableChanges.Select(p => p.ProductId).ToArray());
        Assert.Equal(report.Volume, product.Volume);
    }

    [Fact]
    public async Task DraftsAndCancelledOrders_AreNotPurchases()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Rice");
        var acme = await NewSupplierAsync(organization, "Acme");
        await OrderAsync(organization, acme, rice, 10m, 100m, Day(5, 5));
        await OrderAsync(organization, acme, rice, 10m, 500m, Day(5, 10), Outcome.Draft);
        await OrderAsync(organization, acme, rice, 10m, 900m, Day(5, 11), Outcome.Cancelled);

        var product = Assert.Single((await ReportAsync(organization)).Value.TopProducts);

        var supplier = Assert.Single(product.Suppliers);
        Assert.Equal((100m, "insufficient_history"), (supplier.Latest.UnitCost, supplier.Status));
        Assert.Equal((1, 1000m), (product.Volume.Orders, product.Volume.Spend));
    }

    [Fact]
    public async Task OnlyTheRecentWindow_IsVolume_ButTheLookbackFeedsTheComparison()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Rice");
        var acme = await NewSupplierAsync(organization, "Acme");
        await OrderAsync(organization, acme, rice, 10m, 100m, new DateTimeOffset(2025, 7, 1, 9, 0, 0, TimeSpan.Zero)); // within 365 days
        await OrderAsync(organization, acme, rice, 10m, 50m, new DateTimeOffset(2025, 1, 15, 9, 0, 0, TimeSpan.Zero)); // older than the lookback: ignored
        await OrderAsync(organization, acme, rice, 4m, 120m, Day(5, 20));

        var report = (await ReportAsync(organization)).Value;

        var supplier = Assert.Single(Assert.Single(report.TopProducts).Suppliers);
        Assert.Equal((120m, 100m, 20m), (supplier.Latest.UnitCost, supplier.Previous!.UnitCost, supplier.ChangePercent));
        Assert.Equal((1, 4m, 480m), (report.Volume.Orders, report.Volume.Quantity, report.Volume.Spend));
    }

    [Fact]
    public async Task TheVolume_ReconcilesWithTheStoredOrderLines()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Rice");
        var oil = await NewProductAsync(organization, "Oil");
        var acme = await NewSupplierAsync(organization, "Acme");
        await OrderAsync(organization, acme, rice, 3m, 33.3333m, Day(5, 5));
        await OrderAsync(organization, acme, oil, 7m, 12.5m, Day(5, 6), Outcome.Received);
        await OrderAsync(organization, acme, rice, 1.5m, 99.9m, Day(5, 7));

        var report = (await ReportAsync(organization)).Value;

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(round(l."UnitCost" * l."OrderedQuantity", 4)), 0)
            FROM "PurchaseOrderLines" l JOIN "PurchaseOrders" o ON o."Id" = l."PurchaseOrderId" AND o."OrganizationId" = l."OrganizationId"
            WHERE o."OrganizationId" = @org AND o."Status" IN ('Placed', 'PartiallyReceived', 'Received')
              AND o."PlacedAtUtc" >= @start AND o."PlacedAtUtc" < @end
            """;
        command.Parameters.AddWithValue("org", organization);
        command.Parameters.AddWithValue("start", report.Period.StartUtc);
        command.Parameters.AddWithValue("end", report.Period.EndUtc);
        var stored = Convert.ToDecimal(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

        Assert.Equal(stored, report.Volume.Spend);
        Assert.Equal(3, report.Volume.Orders);
    }

    [Fact]
    public async Task APurchaseWithNoRecordedCost_IsCountedAndNeverCompared()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Rice");
        var acme = await NewSupplierAsync(organization, "Acme");
        await OrderAsync(organization, acme, rice, 10m, 100m, Day(4, 1));
        await OrderAsync(organization, acme, rice, 10m, 0m, Day(5, 10));

        var product = Assert.Single((await ReportAsync(organization)).Value.TopProducts);

        Assert.Equal(1, product.PurchasesWithoutCost);
        Assert.Equal("insufficient_history", Assert.Single(product.Suppliers).Status);
    }

    [Fact]
    public async Task TheTop_LimitsTheProductsListed_NotTheVolumeTotals()
    {
        var organization = await NewOrganizationAsync();
        var acme = await NewSupplierAsync(organization, "Acme");
        foreach (var (name, cost) in new[] { ("A", 10m), ("B", 20m), ("C", 30m) })
        {
            await OrderAsync(organization, acme, await NewProductAsync(organization, name), 1m, cost, Day(5, 10));
        }

        var report = (await ReportAsync(organization, top: 2)).Value;

        Assert.Equal(["C", "B"], report.TopProducts.Select(p => p.Name).ToArray());
        Assert.Equal((3, 60m), (report.Volume.Orders, report.Volume.Spend));
    }

    [Theory]
    [InlineData(0, null, "purchase_costs.days_invalid", "days")]
    [InlineData(93, null, "purchase_costs.days_invalid", "days")]
    [InlineData(null, 0, "purchase_costs.top_invalid", "top")]
    [InlineData(null, 51, "purchase_costs.top_invalid", "top")]
    public async Task OutOfRangeParameters_AreRefused(int? days, int? top, string code, string field)
    {
        var organization = await NewOrganizationAsync();

        var result = await ReportAsync(organization, days, top);

        Assert.Equal((code, field, ErrorKind.Validation), (result.Error!.Code, result.Error.Field, result.Error.Kind));
    }

    [Fact]
    public async Task AnotherOrganizationsPurchases_AreNeverIncluded()
    {
        var mine = await NewOrganizationAsync();
        var theirs = await NewOrganizationAsync();
        var mineProduct = await NewProductAsync(mine, "Mine");
        var theirProduct = await NewProductAsync(theirs, "Theirs");
        var mineSupplier = await NewSupplierAsync(mine, "Mine Ltd");
        var theirSupplier = await NewSupplierAsync(theirs, "Theirs Ltd");
        await OrderAsync(mine, mineSupplier, mineProduct, 1m, 10m, Day(4, 1));
        await OrderAsync(mine, mineSupplier, mineProduct, 1m, 20m, Day(5, 10));
        await OrderAsync(theirs, theirSupplier, theirProduct, 99m, 10m, Day(4, 1));
        await OrderAsync(theirs, theirSupplier, theirProduct, 99m, 90m, Day(5, 10));

        var report = (await ReportAsync(mine)).Value;
        var insights = (await InsightsAsync(mine)).Value;

        Assert.Equal(["Mine"], report.TopProducts.Select(p => p.Name).ToArray());
        Assert.Equal(20m, report.Volume.Spend);
        Assert.Equal(["Mine"], insights.Items.Select(i => i.Subject.Name).ToArray());
    }

    [Theory]
    [InlineData(Role.Owner, true)]
    [InlineData(Role.Manager, true)]
    [InlineData(Role.Staff, false)]
    public async Task OnlyThoseWhoMaySeeCosts_MaySeeThePurchaseCosts(Role role, bool allowed)
    {
        var organization = await NewOrganizationAsync();

        var report = await ReportAsync(organization, role: role);
        var insights = await InsightsAsync(organization, role);

        Assert.Equal(allowed, report.IsSuccess);
        Assert.Equal(allowed, insights.IsSuccess);
        if (!allowed)
        {
            Assert.Equal(ErrorKind.Forbidden, report.Error!.Kind);
        }
    }

    // ---- the insights

    [Fact]
    public async Task TheInsights_ShowTheTwoCostsDatesAndSuppliers()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Rice");
        var acme = await NewSupplierAsync(organization, "Acme");
        var bravo = await NewSupplierAsync(organization, "Bravo");
        await OrderAsync(organization, acme, rice, 10m, 100m, Day(4, 1));
        await OrderAsync(organization, acme, rice, 10m, 125m, Day(5, 10));
        await OrderAsync(organization, bravo, rice, 10m, 90m, Day(5, 11));

        var result = await InsightsAsync(organization);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal([PurchaseCostRule.CostChangeType, PurchaseCostRule.SupplierGapType], result.Value.Items.Select(i => i.Type).ToArray());
        var change = result.Value.Items[0];
        Assert.Equal("Medium", change.Severity);
        Assert.Contains(change.Facts, f => f.Key == "supplier_change_1" && f.Value == "Acme | 100 on 2026-04-01 to 125 on 2026-05-10 | +25%");
        var gap = result.Value.Items[1];
        Assert.Contains(gap.Facts, f => f.Key == "cheapest_supplier" && f.Value == "Bravo");
        Assert.Contains(gap.Facts, f => f.Key == "most_expensive_supplier" && f.Value == "Acme");
    }

    [Fact]
    public async Task ASinglePurchase_AndAnOldChange_ProduceNoInsights()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Rice");
        var oil = await NewProductAsync(organization, "Oil");
        var acme = await NewSupplierAsync(organization, "Acme");
        await OrderAsync(organization, acme, rice, 10m, 100m, Day(5, 10));
        await OrderAsync(organization, acme, oil, 10m, 10m, Day(2, 1));
        await OrderAsync(organization, acme, oil, 10m, 30m, Day(3, 1));

        var result = await InsightsAsync(organization);

        Assert.Empty(result.Value.Items);
    }
}
