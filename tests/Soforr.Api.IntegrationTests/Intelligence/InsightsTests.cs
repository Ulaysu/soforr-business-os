using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Intelligence;
using Soforr.Application.Intelligence.GetInsights;
using Soforr.Application.Intelligence.Sources;
using Soforr.Application.Inventory;
using Soforr.Application.Inventory.ListLowStock;
using Soforr.Application.Organizations;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Identity;
using Soforr.Domain.Inventory;
using Soforr.Domain.Organizations;
using Soforr.Domain.Sales;
using Soforr.Infrastructure.Persistence;
using static Soforr.Api.IntegrationTests.Identity.AuthClient;

namespace Soforr.Api.IntegrationTests.Intelligence;

/// <summary>
/// The insights on real PostgreSQL: gathered from authoritative records through the tenant-filtered context, judged by the
/// pure rule, reproducible for the same data and clock, never stored, and consistent with the low-stock list. The clock is
/// fixed at 2026-06-01 10:00 UTC, so the 30-day window is 2026-05-02 to 2026-05-31 in the organization's time zone.
/// </summary>
[Trait("Category", "Integration")]
public sealed class InsightsTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly Currency Gmd = Currency.Create("GMD");

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }

    private sealed class NewIds : IIdGenerator
    {
        public Guid NewId() => Guid.CreateVersion7();
    }

    private sealed class FakeUser(Guid? userId, Guid? organizationId, Role? role) : ICurrentUser, IOrganizationContext
    {
        public Guid? UserId { get; } = userId;

        public Guid? OrganizationId { get; } = organizationId;

        public Role? Role { get; } = role;

        public static FakeUser In(Guid organizationId, Role role) => new(Guid.CreateVersion7(), organizationId, role);
    }

    private async Task<Guid> NewOrganizationAsync(string timeZone = "UTC")
    {
        var id = Guid.CreateVersion7();
        await using var db = database.CreateContext();
        db.Set<Organization>().Add(Organization.Create(id, "Soforr Trading", Gmd, timeZone, Now));
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> NewProductAsync(Guid organization, string name, decimal stock, decimal? reorderPoint = 10m, int leadTime = 7, bool active = true)
    {
        var settings = reorderPoint is { } point ? ReorderSettings.Create(Quantity.Create(point), leadTime) : null;
        var product = Product.Create(Guid.CreateVersion7(), organization, "SKU-" + Guid.NewGuid().ToString("N"), name, "bag", Money.Create(10m, Gmd), Money.Create(5m, Gmd), settings, Now);
        if (!active)
        {
            product.Deactivate(Now);
        }

        await using (var db = database.CreateContext(organization))
        {
            db.Set<Product>().Add(product);
            await db.SaveChangesAsync();
        }

        if (stock > 0m)
        {
            await RecordAsync(organization, product.Id, stock, InventoryMovementReason.PurchaseReceived);
        }

        return product.Id;
    }

    private async Task RecordAsync(Guid organization, Guid product, decimal delta, InventoryMovementReason reason)
    {
        await using var db = database.CreateContext(organization);
        var user = FakeUser.In(organization, Role.Owner);
        var recorded = await new StockMovementRecorder(db, user, new FixedClock(), new NewIds()).RecordAsync(
            new RecordMovementCommand(product, delta, reason, reason == InventoryMovementReason.PurchaseReceived ? InventorySourceType.PurchaseReceipt : InventorySourceType.Adjustment, Guid.CreateVersion7(), user.UserId!.Value, reason == InventoryMovementReason.ManualAdjustment ? "Adjusted" : null),
            CancellationToken.None);
        Assert.True(recorded.IsSuccess, recorded.Error?.Code);
    }

    /// <summary>Records a completed sale of <paramref name="quantity"/> of a product at the given instant, optionally voiding it afterwards.</summary>
    private async Task SaleAsync(Guid organization, Guid product, decimal quantity, DateTimeOffset at, bool voided = false)
    {
        var price = Money.Create(10m, Gmd);
        var line = new SaleLineInput(Guid.CreateVersion7(), product, "SKU", "Product", "bag", Quantity.Create(quantity), price, Money.Create(10m, Gmd), Money.Create(5m, Gmd), null);
        var sale = Sale.Complete(Guid.CreateVersion7(), organization, Gmd, Guid.CreateVersion7(), at, [line]);
        if (voided)
        {
            sale.Void(Guid.CreateVersion7(), "Test void", at.AddMinutes(5));
        }

        await using var db = database.CreateContext(organization);
        db.Set<Sale>().Add(sale);
        await db.SaveChangesAsync();
    }

    private async Task<Result<InsightsDto>> InsightsAsync(Guid organization, Role role = Role.Owner)
    {
        var user = FakeUser.In(organization, role);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).Options, user);
        var handler = new GetInsightsHandler([new InventoryRiskInsightSource(db)], new FixedClock(), user);
        return await handler.HandleAsync(new GetInsightsQuery(), CancellationToken.None);
    }

    private async Task<Guid[]> LowStockListAsync(Guid organization)
    {
        var user = FakeUser.In(organization, Role.Owner);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).Options, user);
        var result = await new ListLowStockHandler(db, user).HandleAsync(new ListLowStockQuery(1, 100), CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Code);
        return result.Value.Items.Select(item => item.ProductId).Order().ToArray();
    }

    private static string Fact(InsightDto insight, string key) => insight.Facts.Single(f => f.Key == key).Value;

    private static DateTimeOffset Utc(int month, int day, int hour = 0, int minute = 0, int second = 0) => new(2026, month, day, hour, minute, second, TimeSpan.Zero);

    // ---- the reorder point

    [Fact]
    public async Task TheInsights_AreBuiltFromTheRecords_WithFactsAReasonAndAnAction()
    {
        var organization = await NewOrganizationAsync();
        var low = await NewProductAsync(organization, "Rice 25kg", stock: 4m, reorderPoint: 10m, leadTime: 7);
        await NewProductAsync(organization, "Oil", stock: 50m, reorderPoint: 10m);

        var result = await InsightsAsync(organization);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal(Now, result.Value.EvaluatedAtUtc);
        var insight = Assert.Single(result.Value.Items);
        Assert.Equal("inventory.reorder_point_reached", insight.Type);
        Assert.Equal("Medium", insight.Severity);
        Assert.Equal(new InsightSubject("Product", low, "Rice 25kg"), insight.Subject);
        Assert.Equal(["4", "10", "7"], insight.Facts.Where(f => f.Key is "stock" or "reorder_point" or "lead_time_days").Select(f => f.Value).ToArray());
        Assert.Equal("Rice 25kg is at or below its reorder point: 4 bag in stock, reorder point 10. It has had no sales in the last 30 days.", insight.Reason);
        Assert.Equal("create_purchase_order", insight.RecommendedAction.Code);
        Assert.Equal("no recent sales", Fact(insight, "coverage"));
        Assert.Equal("6", Fact(insight, "suggested_order_quantity"));
    }

    [Fact]
    public async Task AProductThatNeverHadStock_IsOutOfStock_AndHighSeverity_AndComesFirst()
    {
        var organization = await NewOrganizationAsync();
        await NewProductAsync(organization, "Some left", stock: 3m, reorderPoint: 10m);
        var none = await NewProductAsync(organization, "None ever", stock: 0m, reorderPoint: 10m);

        var result = await InsightsAsync(organization);

        Assert.Equal(["None ever", "Some left"], result.Value.Items.Select(i => i.Subject.Name).ToArray());
        Assert.Equal("High", result.Value.Items[0].Severity);
        Assert.Equal(none, result.Value.Items[0].Subject.Id);
        Assert.StartsWith("None ever is out of stock (reorder point 10 bag).", result.Value.Items[0].Reason);
    }

    [Fact]
    public async Task InactiveProducts_AndProductsWithoutReorderSettings_AreNotReported_EvenWithSales()
    {
        var organization = await NewOrganizationAsync();
        var inactive = await NewProductAsync(organization, "Inactive", stock: 0m, reorderPoint: 10m, active: false);
        var noSettings = await NewProductAsync(organization, "No settings", stock: 0m, reorderPoint: null);
        await SaleAsync(organization, inactive, 100m, Utc(5, 10));
        await SaleAsync(organization, noSettings, 100m, Utc(5, 10));

        var result = await InsightsAsync(organization);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Items);
    }

    // ---- sales velocity, coverage and the suggested quantity, from real sales

    [Fact]
    public async Task TheVelocityCoverageAndSuggestion_AreComputedFromTheCompletedSalesInTheWindow()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Rice", stock: 4m, reorderPoint: 10m, leadTime: 7);
        await SaleAsync(organization, rice, 30m, Utc(5, 10, 9));
        await SaleAsync(organization, rice, 30m, Utc(5, 20, 15));

        var insight = Assert.Single((await InsightsAsync(organization)).Value.Items);

        // 60 sold in 30 days = 2 a day; 4 in stock lasts 2 days; restocking takes 7; suggest 10 + 2 x 7 - 4 = 20.
        Assert.Equal("High", insight.Severity);
        Assert.Equal("60", Fact(insight, "sold_in_window"));
        Assert.Equal("2", Fact(insight, "average_daily_sales"));
        Assert.Equal("2", Fact(insight, "coverage_days"));
        Assert.Equal("20", Fact(insight, "suggested_order_quantity"));
        Assert.Equal("30", Fact(insight, "window_days"));
        Assert.Equal("2026-05-02 to 2026-05-31 (UTC)", Fact(insight, "window"));
        Assert.Equal(
            "Rice is at or below its reorder point: 4 bag in stock, reorder point 10. At about 2 bag/day over the last 30 days it lasts about 2 days, and restocking takes 7 days.",
            insight.Reason);
    }

    [Fact]
    public async Task AProductAboveItsReorderPoint_ThatWillRunOutBeforeARestock_IsReported()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Fast rice", stock: 20m, reorderPoint: 10m, leadTime: 7);
        await NewProductAsync(organization, "Slow rice", stock: 20m, reorderPoint: 10m, leadTime: 7);
        await SaleAsync(organization, rice, 150m, Utc(5, 15));

        var insight = Assert.Single((await InsightsAsync(organization)).Value.Items);

        Assert.Equal("inventory.runs_out_before_restock", insight.Type);
        Assert.Equal("High", insight.Severity);
        Assert.Equal("Fast rice", insight.Subject.Name);
        Assert.Equal("4", Fact(insight, "coverage_days"));
        Assert.Equal("25", Fact(insight, "suggested_order_quantity"));
    }

    [Fact]
    public async Task TheWindowBoundaries_AreHalfOpen_InTheOrganizationsDays()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization, "Rice", stock: 1m, reorderPoint: 10m, leadTime: 7);
        await SaleAsync(organization, product, 1m, Utc(5, 1, 23, 59, 59)); // the day before the window starts
        await SaleAsync(organization, product, 2m, Utc(5, 2));              // the first instant of the window: counted
        await SaleAsync(organization, product, 4m, Utc(5, 31, 23, 59, 59)); // the last instant of the window: counted
        await SaleAsync(organization, product, 8m, Utc(6, 1));              // the start of today: not counted

        var insight = Assert.Single((await InsightsAsync(organization)).Value.Items);

        Assert.Equal("6", Fact(insight, "sold_in_window"));
    }

    [Fact]
    public async Task TheOrganizationsTimeZone_DecidesWhichSalesAreInTheWindow()
    {
        // Tokyo is UTC+9: its window is local May 2 to May 31, which is 2026-05-01T15:00Z (inclusive) to 2026-05-31T15:00Z (exclusive).
        var organization = await NewOrganizationAsync("Asia/Tokyo");
        var product = await NewProductAsync(organization, "Rice", stock: 1m, reorderPoint: 10m, leadTime: 7);
        await SaleAsync(organization, product, 1m, Utc(5, 1, 14, 59));  // 23:59 local on May 1: before the window
        await SaleAsync(organization, product, 2m, Utc(5, 1, 15));      // 00:00 local on May 2: counted
        await SaleAsync(organization, product, 4m, Utc(5, 31, 14, 59)); // 23:59 local on May 31: counted
        await SaleAsync(organization, product, 8m, Utc(5, 31, 15));     // 00:00 local on June 1 (today): not counted

        var insight = Assert.Single((await InsightsAsync(organization)).Value.Items);

        Assert.Equal("6", Fact(insight, "sold_in_window"));
        Assert.Equal("2026-05-02 to 2026-05-31 (Asia/Tokyo)", Fact(insight, "window"));
    }

    [Fact]
    public async Task AVoidedSale_IsNotCountedAsASale()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization, "Rice", stock: 4m, reorderPoint: 10m, leadTime: 7);
        await SaleAsync(organization, product, 3m, Utc(5, 10));
        await SaleAsync(organization, product, 100m, Utc(5, 11), voided: true);

        var insight = Assert.Single((await InsightsAsync(organization)).Value.Items);

        Assert.Equal("3", Fact(insight, "sold_in_window"));
    }

    [Fact]
    public async Task AnotherOrganizationsSales_AreNeverCounted()
    {
        var first = await NewOrganizationAsync();
        var second = await NewOrganizationAsync();
        var mine = await NewProductAsync(first, "Mine", stock: 4m, reorderPoint: 10m, leadTime: 7);
        var theirs = await NewProductAsync(second, "Theirs", stock: 4m, reorderPoint: 10m, leadTime: 7);
        await SaleAsync(second, theirs, 500m, Utc(5, 10));
        await SaleAsync(first, mine, 3m, Utc(5, 10));

        var mineInsight = Assert.Single((await InsightsAsync(first)).Value.Items);
        var theirInsight = Assert.Single((await InsightsAsync(second)).Value.Items);

        Assert.Equal("3", Fact(mineInsight, "sold_in_window"));
        Assert.Equal("500", Fact(theirInsight, "sold_in_window"));
    }

    [Fact]
    public async Task SalesOfOtherProducts_DoNotCountForAProduct()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Rice", stock: 4m, reorderPoint: 10m, leadTime: 7);
        var oil = await NewProductAsync(organization, "Oil", stock: 4m, reorderPoint: 10m, leadTime: 7);
        await SaleAsync(organization, oil, 90m, Utc(5, 10));

        var items = (await InsightsAsync(organization)).Value.Items;

        Assert.Equal("0", Fact(items.Single(i => i.Subject.Id == rice), "sold_in_window"));
        Assert.Equal("90", Fact(items.Single(i => i.Subject.Id == oil), "sold_in_window"));
    }

    [Fact]
    public async Task NoSalesInTheWindow_MeansNoRecentSales_NotZeroCoverageOrInfinity()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization, "Rice", stock: 4m, reorderPoint: 10m, leadTime: 7);
        await SaleAsync(organization, product, 50m, Utc(4, 1)); // long before the window

        var insight = Assert.Single((await InsightsAsync(organization)).Value.Items);

        Assert.Equal("0", Fact(insight, "sold_in_window"));
        Assert.Equal("no recent sales", Fact(insight, "coverage"));
        Assert.DoesNotContain(insight.Facts, f => f.Key == "coverage_days");
    }

    // ---- reproducibility, consistency, isolation

    [Fact]
    public async Task TheSameDataAndClock_GiveTheSameInsights_EveryTime()
    {
        var organization = await NewOrganizationAsync();
        for (var i = 0; i < 6; i++)
        {
            var product = await NewProductAsync(organization, "Product " + i, stock: i, reorderPoint: 3m);
            await SaleAsync(organization, product, i * 10m + 1m, Utc(5, 12));
        }

        var first = await InsightsAsync(organization);
        var second = await InsightsAsync(organization);

        Assert.Equal(first.Value.EvaluatedAtUtc, second.Value.EvaluatedAtUtc);
        Assert.Equal(
            first.Value.Items.Select(i => (i.Key, i.Type, i.Severity, i.Reason, string.Join("|", i.Facts.Select(f => f.Key + "=" + f.Value)))).ToArray(),
            second.Value.Items.Select(i => (i.Key, i.Type, i.Severity, i.Reason, string.Join("|", i.Facts.Select(f => f.Key + "=" + f.Value)))).ToArray());
        Assert.True(first.Value.Items.Count >= 4);
    }

    [Fact]
    public async Task TheInsights_FollowTheStockAsItChanges_BecauseNothingIsStored()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization, "Rice", stock: 4m, reorderPoint: 10m);
        Assert.Single((await InsightsAsync(organization)).Value.Items);

        await RecordAsync(organization, product, 20m, InventoryMovementReason.PurchaseReceived);
        var restocked = await InsightsAsync(organization);
        await RecordAsync(organization, product, -24m, InventoryMovementReason.ManualAdjustment);
        var emptied = await InsightsAsync(organization);

        Assert.Empty(restocked.Value.Items);
        var insight = Assert.Single(emptied.Value.Items);
        Assert.Equal("High", insight.Severity);
        Assert.Equal("0", Fact(insight, "stock"));
    }

    [Fact]
    public async Task TheReorderPointInsights_NameExactlyTheProductsOfTheLowStockList()
    {
        var organization = await NewOrganizationAsync();
        await NewProductAsync(organization, "A", stock: 0m, reorderPoint: 5m);
        await NewProductAsync(organization, "B", stock: 5m, reorderPoint: 5m);
        await NewProductAsync(organization, "C", stock: 5.0001m, reorderPoint: 5m);
        await NewProductAsync(organization, "D", stock: 1m, reorderPoint: null);
        await NewProductAsync(organization, "E", stock: 0m, reorderPoint: 0m);
        await NewProductAsync(organization, "F", stock: 0m, reorderPoint: 5m, active: false);

        var insights = (await InsightsAsync(organization)).Value.Items.Where(i => i.Type == "inventory.reorder_point_reached").Select(i => i.Subject.Id!.Value).Order().ToArray();

        Assert.Equal(await LowStockListAsync(organization), insights);
        Assert.Equal(3, insights.Length);
    }

    [Fact]
    public async Task AnotherOrganizationsProducts_NeverAppear()
    {
        var first = await NewOrganizationAsync();
        var second = await NewOrganizationAsync();
        await NewProductAsync(first, "First's", stock: 1m, reorderPoint: 5m);
        await NewProductAsync(second, "Second's", stock: 1m, reorderPoint: 5m);
        await NewProductAsync(second, "Second's other", stock: 0m, reorderPoint: 5m);

        var mine = await InsightsAsync(first);
        var theirs = await InsightsAsync(second);

        Assert.Equal(["First's"], mine.Value.Items.Select(i => i.Subject.Name).ToArray());
        Assert.Equal(["Second's other", "Second's"], theirs.Value.Items.Select(i => i.Subject.Name).ToArray());
    }

    [Fact]
    public async Task ACallerWithNoOrganization_SeesNothing()
    {
        var organization = await NewOrganizationAsync();
        await NewProductAsync(organization, "Rice", stock: 0m, reorderPoint: 5m);
        var user = new FakeUser(Guid.CreateVersion7(), null, Role.Owner);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).Options, user);

        var result = await new GetInsightsHandler([new InventoryRiskInsightSource(db)], new FixedClock(), user).HandleAsync(new GetInsightsQuery(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Forbidden, result.Error!.Kind);
    }

    [Fact]
    public async Task AStaffMember_IsForbidden()
    {
        var organization = await NewOrganizationAsync();
        await NewProductAsync(organization, "Rice", stock: 0m, reorderPoint: 5m);

        var result = await InsightsAsync(organization, Role.Staff);

        Assert.True(result.IsFailure);
        Assert.Equal("identity.forbidden", result.Error!.Code);
    }

    [Fact]
    public async Task NoInsightIsStored_TheDatabaseHasNoInsightTable()
    {
        await using var db = database.CreateContext();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND lower(table_name) LIKE '%insight%'";

        Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheRunningApplication_RegistersTheSourcesAndTheHandler()
    {
        using var factory = CreateFactory(database.ConnectionString);
        using var scope = factory.Services.CreateScope();

        var sources = scope.ServiceProvider.GetServices<IInsightSource>().ToArray();
        var handler = scope.ServiceProvider.GetRequiredService<IQueryHandler<GetInsightsQuery, InsightsDto>>();

        Assert.Contains(sources, source => source is InventoryRiskInsightSource);
        Assert.Contains(sources, source => source is SalesInsightSource);
        Assert.Contains(sources, source => source is MarginInsightSource);
        Assert.IsType<GetInsightsHandler>(handler);
    }
}
