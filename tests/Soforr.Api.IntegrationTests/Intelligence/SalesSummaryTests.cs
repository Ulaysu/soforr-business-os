using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Intelligence;
using Soforr.Application.Intelligence.GetInsights;
using Soforr.Application.Intelligence.GetSalesSummary;
using Soforr.Application.Intelligence.Sources;
using Soforr.Application.Organizations;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Identity;
using Soforr.Domain.Organizations;
using Soforr.Domain.Sales;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Api.IntegrationTests.Intelligence;

/// <summary>
/// The sales summary and the sales insights on real PostgreSQL. The clock is fixed at 2026-06-01 10:00 UTC, so with the default
/// 30-day periods the current period is 2026-05-02 to 2026-05-31 and the previous one 2026-04-02 to 2026-05-01 (in the
/// organization's time zone). Revenue is reconciled against a raw SQL sum of the stored sale lines.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SalesSummaryTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LongAgo = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
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

    private async Task<Guid> NewOrganizationAsync(string timeZone = "UTC")
    {
        var id = Guid.CreateVersion7();
        await using var db = database.CreateContext();
        db.Set<Organization>().Add(Organization.Create(id, "Soforr Trading", Gmd, timeZone, LongAgo));
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> NewProductAsync(Guid organization, string name, bool active = true, DateTimeOffset? created = null, decimal price = 10m)
    {
        var product = Product.Create(Guid.CreateVersion7(), organization, "SKU-" + Guid.NewGuid().ToString("N"), name, "bag", Money.Create(price, Gmd), Money.Create(5m, Gmd), null, created ?? LongAgo);
        if (!active)
        {
            product.Deactivate(created ?? LongAgo);
        }

        await using var db = database.CreateContext(organization);
        db.Set<Product>().Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    /// <summary>Records a completed sale of one product at the given instant, optionally voided afterwards.</summary>
    private async Task SaleAsync(Guid organization, Guid product, decimal quantity, DateTimeOffset at, decimal price = 10m, bool voided = false)
    {
        var line = new SaleLineInput(Guid.CreateVersion7(), product, "SKU", "Product", "bag", Quantity.Create(quantity), Money.Create(price, Gmd), Money.Create(price, Gmd), Money.Create(5m, Gmd), null);
        var sale = Sale.Complete(Guid.CreateVersion7(), organization, Gmd, Guid.CreateVersion7(), at, [line]);
        if (voided)
        {
            sale.Void(Guid.CreateVersion7(), "Test void", at.AddMinutes(5));
        }

        await using var db = database.CreateContext(organization);
        db.Set<Sale>().Add(sale);
        await db.SaveChangesAsync();
    }

    private async Task<Result<SalesSummaryDto>> SummaryAsync(Guid organization, int? days = null, int? top = null, Role role = Role.Owner)
    {
        var user = FakeUser.In(organization, role);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).Options, user);
        return await new GetSalesSummaryHandler(db, new FixedClock(), user).HandleAsync(new GetSalesSummaryQuery(days, top), CancellationToken.None);
    }

    private async Task<Result<InsightsDto>> InsightsAsync(Guid organization)
    {
        var user = FakeUser.In(organization, Role.Owner);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).Options, user);
        return await new GetInsightsHandler([new SalesInsightSource(db)], new FixedClock(), user).HandleAsync(new GetInsightsQuery(), CancellationToken.None);
    }

    private static DateTimeOffset Utc(int month, int day, int hour = 12, int minute = 0, int second = 0) => new(2026, month, day, hour, minute, second, TimeSpan.Zero);

    // ---- revenue, growth, periods

    [Fact]
    public async Task TheSummary_ComparesTheCurrentPeriodWithTheOneBeforeIt_AndNamesBothPeriods()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Rice");
        await SaleAsync(organization, rice, 12m, Utc(5, 10));   // 120, current
        await SaleAsync(organization, rice, 10m, Utc(4, 20));   // 100, previous

        var result = await SummaryAsync(organization);

        Assert.True(result.IsSuccess, result.Error?.Code);
        var summary = result.Value;
        Assert.Equal(("GMD", "UTC"), (summary.Currency, summary.TimeZoneId));
        Assert.Equal((new DateOnly(2026, 5, 2), new DateOnly(2026, 5, 31), 30), (summary.Current.FirstDay, summary.Current.LastDay, summary.Current.Days));
        Assert.Equal((new DateOnly(2026, 4, 2), new DateOnly(2026, 5, 1), 30), (summary.Previous.FirstDay, summary.Previous.LastDay, summary.Previous.Days));
        Assert.Equal(summary.Previous.EndUtc, summary.Current.StartUtc);
        Assert.Equal((120m, 100m, 20m, "ok", 1, 1), (summary.Totals.Revenue, summary.Totals.PreviousRevenue, summary.Totals.RevenueGrowthPercent, summary.Totals.GrowthStatus, summary.Totals.SaleCount, summary.Totals.PreviousSaleCount));
        var top = Assert.Single(summary.TopSellers);
        Assert.Equal(("Rice", 12m, 120m, 10m, 100m, 20m), (top.Name, top.Quantity, top.Revenue, top.PreviousQuantity, top.PreviousRevenue, top.QuantityChangePercent));
    }

    [Fact]
    public async Task WithNoPreviousSales_TheGrowthIsNotAFigure_ButAStatus()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Rice");
        await SaleAsync(organization, rice, 5m, Utc(5, 10));

        var summary = (await SummaryAsync(organization)).Value;

        Assert.Null(summary.Totals.RevenueGrowthPercent);
        Assert.Equal("no_comparable_previous_sales", summary.Totals.GrowthStatus);
        Assert.Equal(50m, summary.Totals.Revenue);
        Assert.Null(Assert.Single(summary.TopSellers).QuantityChangePercent);
    }

    [Fact]
    public async Task WithNoSalesAtAll_TheSummaryIsEmpty_NotAnError()
    {
        var organization = await NewOrganizationAsync();

        var result = await SummaryAsync(organization);

        Assert.True(result.IsSuccess);
        Assert.Equal((0m, 0m, null, "no_comparable_previous_sales", 0, 0), (result.Value.Totals.Revenue, result.Value.Totals.PreviousRevenue, result.Value.Totals.RevenueGrowthPercent, result.Value.Totals.GrowthStatus, result.Value.Totals.SaleCount, result.Value.Totals.PreviousSaleCount));
        Assert.Empty(result.Value.TopSellers);
        Assert.Empty(result.Value.DecliningProducts);
        Assert.Empty(result.Value.SlowMovers.Items);
    }

    [Fact]
    public async Task TheDaysParameter_ChangesThePeriodLength()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Rice");
        await SaleAsync(organization, rice, 1m, Utc(5, 28));  // within the last 7 days
        await SaleAsync(organization, rice, 2m, Utc(5, 20));  // within the 7 days before that
        await SaleAsync(organization, rice, 4m, Utc(5, 1));   // within neither

        var summary = (await SummaryAsync(organization, days: 7)).Value;

        Assert.Equal((new DateOnly(2026, 5, 25), new DateOnly(2026, 5, 31), 7), (summary.Current.FirstDay, summary.Current.LastDay, summary.Current.Days));
        Assert.Equal((10m, 20m), (summary.Totals.Revenue, summary.Totals.PreviousRevenue));
        Assert.Equal(-50m, summary.Totals.RevenueGrowthPercent);
    }

    [Fact]
    public async Task ThePeriodBoundaries_AreHalfOpen_ToTheSecond()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Rice");
        await SaleAsync(organization, rice, 1m, Utc(4, 1, 23, 59, 59)); // before the previous period
        await SaleAsync(organization, rice, 2m, Utc(4, 2, 0));          // first instant of the previous period
        await SaleAsync(organization, rice, 4m, Utc(5, 1, 23, 59, 59)); // last instant of the previous period
        await SaleAsync(organization, rice, 8m, Utc(5, 2, 0));          // first instant of the current period
        await SaleAsync(organization, rice, 16m, Utc(5, 31, 23, 59, 59)); // last instant of the current period
        await SaleAsync(organization, rice, 32m, Utc(6, 1, 0));         // today

        var summary = (await SummaryAsync(organization)).Value;

        Assert.Equal((240m, 60m), (summary.Totals.Revenue, summary.Totals.PreviousRevenue));
    }

    [Fact]
    public async Task TheOrganizationsTimeZone_DecidesWhichSalesAreInWhichPeriod()
    {
        // Tokyo is UTC+9: the current period starts 2026-05-01T15:00Z, the previous one 2026-04-01T15:00Z.
        var organization = await NewOrganizationAsync("Asia/Tokyo");
        var rice = await NewProductAsync(organization, "Rice");
        await SaleAsync(organization, rice, 1m, Utc(5, 1, 14, 59)); // 23:59 local on May 1: previous period
        await SaleAsync(organization, rice, 2m, Utc(5, 1, 15, 0));  // 00:00 local on May 2: current period
        await SaleAsync(organization, rice, 4m, Utc(5, 31, 14, 59)); // 23:59 local on May 31: current period
        await SaleAsync(organization, rice, 8m, Utc(5, 31, 15, 0));  // 00:00 local on June 1: today, neither

        var summary = (await SummaryAsync(organization)).Value;

        Assert.Equal("Asia/Tokyo", summary.TimeZoneId);
        Assert.Equal((60m, 10m), (summary.Totals.Revenue, summary.Totals.PreviousRevenue));
        Assert.Equal(Utc(5, 1, 15, 0), summary.Current.StartUtc);
    }

    [Fact]
    public async Task AVoidedSale_CountsInNeitherPeriod()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Rice");
        await SaleAsync(organization, rice, 3m, Utc(5, 10));
        await SaleAsync(organization, rice, 100m, Utc(5, 11), voided: true);
        await SaleAsync(organization, rice, 200m, Utc(4, 11), voided: true);

        var summary = (await SummaryAsync(organization)).Value;

        Assert.Equal((30m, 0m, 1, 0), (summary.Totals.Revenue, summary.Totals.PreviousRevenue, summary.Totals.SaleCount, summary.Totals.PreviousSaleCount));
        Assert.Equal(3m, Assert.Single(summary.TopSellers).Quantity);
    }

    [Fact]
    public async Task Revenue_ReconcilesWithTheStoredSaleLines_ToTheLastDecimal()
    {
        var organization = await NewOrganizationAsync();
        var oil = await NewProductAsync(organization, "Oil");
        var rice = await NewProductAsync(organization, "Rice");
        var sugar = await NewProductAsync(organization, "Sugar");
        // Fractional quantities and prices make every line total need rounding (half away from zero, four decimals).
        await SaleAsync(organization, oil, 0.3333m, Utc(5, 3), price: 1.5m);
        await SaleAsync(organization, oil, 7.7777m, Utc(5, 9), price: 0.0003m);
        await SaleAsync(organization, rice, 2.5m, Utc(5, 12), price: 3.3333m);
        await SaleAsync(organization, sugar, 0.0005m, Utc(5, 20), price: 1m);
        await SaleAsync(organization, sugar, 11.1111m, Utc(5, 31, 23, 59, 59), price: 2.2222m);
        await SaleAsync(organization, rice, 9.9999m, Utc(4, 5), price: 0.0001m);
        await SaleAsync(organization, rice, 4m, Utc(4, 30, 23, 59, 59), price: 7.7777m);
        await SaleAsync(organization, oil, 100m, Utc(5, 15), price: 9m, voided: true);
        var other = await NewOrganizationAsync();
        await SaleAsync(other, await NewProductAsync(other, "Theirs"), 1000m, Utc(5, 15));

        var summary = (await SummaryAsync(organization)).Value;

        var current = await RawRevenueAsync(organization, Utc(5, 2, 0), Utc(6, 1, 0));
        var previous = await RawRevenueAsync(organization, Utc(4, 2, 0), Utc(5, 2, 0));
        Assert.Equal(current, summary.Totals.Revenue);
        Assert.Equal(previous, summary.Totals.PreviousRevenue);
        Assert.NotEqual(0m, current);
        // And each product's figure matches its own lines, so the products add up to the total.
        Assert.Equal(current, summary.TopSellers.Sum(item => item.Revenue));
        Assert.Equal(previous, summary.TopSellers.Sum(item => item.PreviousRevenue));
    }

    private async Task<decimal> RawRevenueAsync(Guid organization, DateTimeOffset from, DateTimeOffset to)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COALESCE(SUM(ROUND(l.\"UnitPrice\" * l.\"Quantity\", 4)), 0) FROM \"SaleLines\" l " +
            "JOIN \"Sales\" s ON s.\"Id\" = l.\"SaleId\" AND s.\"OrganizationId\" = l.\"OrganizationId\" " +
            "WHERE s.\"OrganizationId\" = @org AND s.\"Status\" = 'Completed' AND s.\"CreatedAtUtc\" >= @from AND s.\"CreatedAtUtc\" < @to";
        command.Parameters.AddWithValue("org", organization);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    // ---- top sellers, declining, slow movers

    [Fact]
    public async Task TopSellers_AreOrderedByQuantity_AndLimitedByTheTopParameter()
    {
        var organization = await NewOrganizationAsync();
        var names = new[] { "A", "B", "C", "D" };
        for (var i = 0; i < names.Length; i++)
        {
            await SaleAsync(organization, await NewProductAsync(organization, names[i]), (i + 1) * 3m, Utc(5, 10));
        }

        var all = (await SummaryAsync(organization)).Value.TopSellers;
        var two = (await SummaryAsync(organization, top: 2)).Value.TopSellers;

        Assert.Equal(["D", "C", "B", "A"], all.Select(s => s.Name).ToArray());
        Assert.Equal(["D", "C"], two.Select(s => s.Name).ToArray());
    }

    [Fact]
    public async Task ADecliningProduct_IsListedWithItsDecline()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Rice");
        var steady = await NewProductAsync(organization, "Steady");
        await SaleAsync(organization, rice, 6m, Utc(5, 10));
        await SaleAsync(organization, rice, 10m, Utc(4, 10));
        await SaleAsync(organization, steady, 9m, Utc(5, 10));
        await SaleAsync(organization, steady, 10m, Utc(4, 10));

        var declining = Assert.Single((await SummaryAsync(organization)).Value.DecliningProducts);

        Assert.Equal(("Rice", 6m, 10m, 40m), (declining.Product.Name, declining.Product.Quantity, declining.Product.PreviousQuantity, declining.DeclinePercent));
    }

    [Fact]
    public async Task SlowMovers_AreTheBottomTwentyPercentOfTheQualifyingProducts()
    {
        var organization = await NewOrganizationAsync();
        for (var i = 0; i < 10; i++)
        {
            var product = await NewProductAsync(organization, $"P{i:D2}");
            if (i >= 2)
            {
                await SaleAsync(organization, product, i, Utc(5, 10));
            }
        }

        await NewProductAsync(organization, "Inactive", active: false);
        await NewProductAsync(organization, "TooNew", created: Utc(5, 20)); // created during the current period

        var slow = (await SummaryAsync(organization)).Value.SlowMovers;

        Assert.False(slow.ShowsFullList);
        Assert.Equal((10, 20), (slow.QualifyingProducts, slow.BottomPercent));
        Assert.Equal(["P00", "P01"], slow.Items.Select(i => i.Name).ToArray());
        Assert.All(slow.Items, item => Assert.Equal(0m, item.Quantity));
    }

    [Fact]
    public async Task WithFewerThanFiveQualifyingProducts_TheSummaryListsThemAll_InOrder()
    {
        var organization = await NewOrganizationAsync();
        var a = await NewProductAsync(organization, "A");
        await NewProductAsync(organization, "B");
        await SaleAsync(organization, a, 4m, Utc(5, 10));

        var slow = (await SummaryAsync(organization)).Value.SlowMovers;

        Assert.True(slow.ShowsFullList);
        Assert.Equal(["B", "A"], slow.Items.Select(i => i.Name).ToArray());
    }

    // ---- the sales insights

    [Fact]
    public async Task TheInsights_NameDecliningProductsAndTheSlowestSellers_WithTheirFacts()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Rice");
        await SaleAsync(organization, rice, 6m, Utc(5, 10));
        await SaleAsync(organization, rice, 10m, Utc(4, 10));
        for (var i = 0; i < 5; i++)
        {
            var product = await NewProductAsync(organization, $"Filler {i}");
            await SaleAsync(organization, product, 50m + i, Utc(5, 12));
            await SaleAsync(organization, product, 50m + i, Utc(4, 12));
        }

        var items = (await InsightsAsync(organization)).Value.Items;

        var declining = Assert.Single(items, i => i.Type == "sales.declining_product");
        Assert.Equal(("Low", "Rice", "40"), (declining.Severity, declining.Subject.Name, declining.Facts.Single(f => f.Key == "decline_percent").Value));
        Assert.Equal("Rice sold 6 bag in the last 30 days, down 40% from 10 bag in the 30 days before.", declining.Reason);
        Assert.Equal("2026-05-02 to 2026-05-31 (UTC)", declining.Facts.Single(f => f.Key == "period").Value);
        Assert.Equal("2026-04-02 to 2026-05-01 (UTC)", declining.Facts.Single(f => f.Key == "previous_period").Value);
        Assert.Equal("review_product", declining.RecommendedAction.Code);

        var slow = Assert.Single(items, i => i.Type == "sales.slow_mover");
        Assert.Equal("Rice", slow.Subject.Name); // 6 bag is the least of the six qualifying products
        Assert.Equal("Rice sold only 6 bag in the last 30 days, among the slowest 20% of 6 active products.", slow.Reason);
        Assert.Equal("6", slow.Facts.Single(f => f.Key == "qualifying_products").Value);
        Assert.All(items, item => Assert.NotEmpty(item.Facts));
    }

    [Fact]
    public async Task OnAVerySmallCatalog_NoProductIsFlaggedSlow_ButTheSummaryStillListsThem()
    {
        var organization = await NewOrganizationAsync();
        await NewProductAsync(organization, "A");
        await NewProductAsync(organization, "B");

        var items = (await InsightsAsync(organization)).Value.Items;

        Assert.Empty(items);
        Assert.True((await SummaryAsync(organization)).Value.SlowMovers.ShowsFullList);
    }

    [Fact]
    public async Task AProductWithNoSalesAtAll_IsAZeroSale_InTheSlowInsight()
    {
        var organization = await NewOrganizationAsync();
        for (var i = 0; i < 5; i++)
        {
            var product = await NewProductAsync(organization, $"P{i}");
            if (i > 0)
            {
                await SaleAsync(organization, product, 9m, Utc(5, 12));
            }
        }

        var slow = Assert.Single((await InsightsAsync(organization)).Value.Items);

        Assert.Equal("sales.slow_mover", slow.Type);
        Assert.Equal("P0 had no sales in the last 30 days, among the slowest 20% of 5 active products.", slow.Reason);
    }

    [Fact]
    public async Task TheInsights_AreReproducible_ForTheSameDataAndClock()
    {
        var organization = await NewOrganizationAsync();
        for (var i = 0; i < 8; i++)
        {
            var product = await NewProductAsync(organization, $"P{i}");
            await SaleAsync(organization, product, 10m - i, Utc(5, 12));
            await SaleAsync(organization, product, 10m, Utc(4, 12));
        }

        var first = (await InsightsAsync(organization)).Value.Items.Select(i => (i.Key, i.Reason)).ToArray();
        var second = (await InsightsAsync(organization)).Value.Items.Select(i => (i.Key, i.Reason)).ToArray();

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }

    // ---- isolation, permissions, validation

    [Fact]
    public async Task AnotherOrganizationsSalesAndProducts_NeverAppear()
    {
        var first = await NewOrganizationAsync();
        var second = await NewOrganizationAsync();
        var mine = await NewProductAsync(first, "Mine");
        var theirs = await NewProductAsync(second, "Theirs");
        await SaleAsync(first, mine, 2m, Utc(5, 10));
        await SaleAsync(second, theirs, 900m, Utc(5, 10));

        var mineSummary = (await SummaryAsync(first)).Value;
        var theirSummary = (await SummaryAsync(second)).Value;

        Assert.Equal(20m, mineSummary.Totals.Revenue);
        Assert.Equal(["Mine"], mineSummary.TopSellers.Select(s => s.Name).ToArray());
        Assert.Equal(["Mine"], mineSummary.SlowMovers.Items.Select(s => s.Name).ToArray());
        Assert.Equal(9000m, theirSummary.Totals.Revenue);
        Assert.Equal(["Theirs"], theirSummary.TopSellers.Select(s => s.Name).ToArray());
    }

    [Fact]
    public async Task AStaffMember_IsForbidden()
    {
        var organization = await NewOrganizationAsync();

        var result = await SummaryAsync(organization, role: Role.Staff);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Forbidden, result.Error!.Kind);
    }

    [Theory]
    [InlineData(0, null, "sales_summary.days_invalid", "days")]
    [InlineData(93, null, "sales_summary.days_invalid", "days")]
    [InlineData(-5, null, "sales_summary.days_invalid", "days")]
    [InlineData(30, 0, "sales_summary.top_invalid", "top")]
    [InlineData(30, 51, "sales_summary.top_invalid", "top")]
    public async Task InvalidParameters_AreRejected_WithTheFieldAndACode(int days, int? top, string code, string field)
    {
        var organization = await NewOrganizationAsync();

        var result = await SummaryAsync(organization, days, top);

        Assert.True(result.IsFailure);
        Assert.Equal((ErrorKind.Validation, code, field), (result.Error!.Kind, result.Error.Code, result.Error.Field));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(92)]
    public async Task TheLimitsOfThePeriodLength_AreAccepted(int days)
    {
        var organization = await NewOrganizationAsync();

        var result = await SummaryAsync(organization, days);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal(days, result.Value.Current.Days);
        Assert.Equal(days, result.Value.Previous.Days);
    }
}
