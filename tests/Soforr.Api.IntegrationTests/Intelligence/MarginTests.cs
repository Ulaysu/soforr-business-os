using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Intelligence;
using Soforr.Application.Intelligence.GetInsights;
using Soforr.Application.Intelligence.GetMarginSummary;
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
/// The margin summary and the margin insights on real PostgreSQL. The clock is fixed at 2026-06-01 10:00 UTC, so with the default
/// 30-day periods the current period is 2026-05-02 to 2026-05-31 and the previous one 2026-04-02 to 2026-05-01. The margins are
/// reconciled against a raw SQL sum of the revenue and cost recorded on the stored sale lines.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MarginTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
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

    private async Task<Guid> NewProductAsync(Guid organization, string name, decimal costPrice = 5m)
    {
        var product = Product.Create(Guid.CreateVersion7(), organization, "SKU-" + Guid.NewGuid().ToString("N"), name, "bag", Money.Create(10m, Gmd), Money.Create(costPrice, Gmd), null, LongAgo);
        await using var db = database.CreateContext(organization);
        db.Set<Product>().Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    /// <summary>Records a completed sale of one product at the given instant, with the price charged and the cost price recorded on the line.</summary>
    private async Task SaleAsync(Guid organization, Guid product, decimal quantity, decimal price, decimal costPrice, DateTimeOffset at, bool voided = false)
    {
        var line = new SaleLineInput(Guid.CreateVersion7(), product, "SKU", "Product", "bag", Quantity.Create(quantity), Money.Create(price, Gmd), Money.Create(price, Gmd), Money.Create(costPrice, Gmd), null);
        var sale = Sale.Complete(Guid.CreateVersion7(), organization, Gmd, Guid.CreateVersion7(), at, [line]);
        if (voided)
        {
            sale.Void(Guid.CreateVersion7(), "Test void", at.AddMinutes(5));
        }

        await using var db = database.CreateContext(organization);
        db.Set<Sale>().Add(sale);
        await db.SaveChangesAsync();
    }

    private async Task<Result<MarginSummaryDto>> SummaryAsync(Guid organization, int? days = null, int? top = null, Role role = Role.Owner)
    {
        var user = FakeUser.In(organization, role);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).Options, user);
        return await new GetMarginSummaryHandler(db, new FixedClock(), user).HandleAsync(new GetMarginSummaryQuery(days, top), CancellationToken.None);
    }

    private async Task<Result<InsightsDto>> InsightsAsync(Guid organization, Role role = Role.Owner)
    {
        var user = FakeUser.In(organization, role);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).Options, user);
        return await new GetInsightsHandler([new MarginInsightSource(db)], new FixedClock(), user).HandleAsync(new GetInsightsQuery(), CancellationToken.None);
    }

    private static DateTimeOffset Utc(int month, int day, int hour = 12, int minute = 0, int second = 0) => new(2026, month, day, hour, minute, second, TimeSpan.Zero);

    // ---- the summary

    [Fact]
    public async Task TheMargin_IsRevenueMinusTheCostRecordedOnTheLines_ComparedWithThePreviousPeriod()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Rice");
        await SaleAsync(organization, rice, 10m, price: 10m, costPrice: 6m, Utc(5, 10));   // 100 revenue, 60 cost -> 40%
        await SaleAsync(organization, rice, 10m, price: 10m, costPrice: 5m, Utc(4, 10));   // 100 revenue, 50 cost -> 50%

        var result = await SummaryAsync(organization);

        Assert.True(result.IsSuccess, result.Error?.Code);
        var summary = result.Value;
        Assert.Equal(("GMD", "UTC"), (summary.Currency, summary.TimeZoneId));
        Assert.Equal((100m, 60m, 40m, 40m, "ok"), (summary.Totals.Current.Revenue, summary.Totals.Current.Cost, summary.Totals.Current.GrossMargin, summary.Totals.Current.MarginPercent, summary.Totals.Current.Status));
        Assert.Equal((50m, -10m), (summary.Totals.Previous.MarginPercent, summary.Totals.MarginChangePoints));
        var product = Assert.Single(summary.TopProducts);
        Assert.Equal(("Rice", 40m, 50m, -10m), (product.Name, product.Current.MarginPercent, product.Previous.MarginPercent, product.MarginChangePoints));
        Assert.Equal((10m, 6m, 10m, 5m), (product.Current.AverageUnitPrice, product.Current.AverageUnitCost, product.Previous.AverageUnitPrice, product.Previous.AverageUnitCost));
    }

    [Fact]
    public async Task TheMargin_UsesTheCostRecordedAtTheTimeOfTheSale_NotTheProductsCostNow()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Rice", costPrice: 5m);
        await SaleAsync(organization, rice, 10m, price: 10m, costPrice: 5m, Utc(5, 10));
        await using (var db = database.CreateContext(organization))
        {
            var product = await db.Set<Product>().SingleAsync(p => p.Id == rice);
            product.Update(product.Sku, "Renamed", "kg", Money.Create(99m, Gmd), Money.Create(90m, Gmd), null, Utc(5, 20));
            await db.SaveChangesAsync();
        }

        var summary = (await SummaryAsync(organization)).Value;

        Assert.Equal((100m, 50m, 50m), (summary.Totals.Current.Revenue, summary.Totals.Current.Cost, summary.Totals.Current.MarginPercent));
    }

    [Fact]
    public async Task TheMargin_UsesTheFinalPriceCharged_NotTheCatalogPrice_OfAnOverriddenLine()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Rice");
        var line = new SaleLineInput(Guid.CreateVersion7(), rice, "SKU", "Rice", "bag", Quantity.Create(10m), Money.Create(10m, Gmd), Money.Create(7m, Gmd), Money.Create(6m, Gmd), "Promo");
        await using (var db = database.CreateContext(organization))
        {
            db.Set<Sale>().Add(Sale.Complete(Guid.CreateVersion7(), organization, Gmd, Guid.CreateVersion7(), Utc(5, 10), [line]));
            await db.SaveChangesAsync();
        }

        var summary = (await SummaryAsync(organization)).Value;

        Assert.Equal((70m, 60m, 10m), (summary.Totals.Current.Revenue, summary.Totals.Current.Cost, summary.Totals.Current.GrossMargin)); // not 100 of catalog revenue
        Assert.Equal(14.3m, summary.Totals.Current.MarginPercent);
    }

    [Fact]
    public async Task LinesWithNoRecordedCost_AreLeftOutOfTheMargin_AndReportedSeparately()
    {
        var organization = await NewOrganizationAsync();
        var costed = await NewProductAsync(organization, "Costed");
        var uncosted = await NewProductAsync(organization, "Uncosted", costPrice: 0m);
        await SaleAsync(organization, costed, 10m, price: 10m, costPrice: 6m, Utc(5, 10));
        await SaleAsync(organization, uncosted, 10m, price: 20m, costPrice: 0m, Utc(5, 11));

        var summary = (await SummaryAsync(organization)).Value;

        Assert.Equal((100m, 60m, 40m, 200m, "partly_costed"), (summary.Totals.Current.Revenue, summary.Totals.Current.Cost, summary.Totals.Current.MarginPercent, summary.Totals.Current.RevenueWithoutCost, summary.Totals.Current.Status));
        var missing = Assert.Single(summary.MissingCost);
        Assert.Equal(("Uncosted", "cost_not_recorded", 200m), (missing.Name, missing.Current.Status, missing.Current.RevenueWithoutCost));
        Assert.Null(missing.Current.MarginPercent);
        Assert.DoesNotContain(summary.TopProducts, p => p.Name == "Uncosted"); // no margin to rank
        Assert.DoesNotContain(summary.NegativeMargin, p => p.Name == "Uncosted");
    }

    [Fact]
    public async Task AVoidedSale_CountsInNeitherPeriod()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Rice");
        await SaleAsync(organization, rice, 10m, price: 10m, costPrice: 6m, Utc(5, 10));
        await SaleAsync(organization, rice, 100m, price: 10m, costPrice: 1m, Utc(5, 11), voided: true);

        var summary = (await SummaryAsync(organization)).Value;

        Assert.Equal((100m, 60m), (summary.Totals.Current.Revenue, summary.Totals.Current.Cost));
    }

    [Fact]
    public async Task ProductsSoldAtALoss_AndProductsWhoseMarginFell_AreListed()
    {
        var organization = await NewOrganizationAsync();
        var loss = await NewProductAsync(organization, "Loss maker");
        var fell = await NewProductAsync(organization, "Fell");
        var steady = await NewProductAsync(organization, "Steady");
        await SaleAsync(organization, loss, 10m, price: 5m, costPrice: 8m, Utc(5, 10));       // -30 margin
        await SaleAsync(organization, fell, 10m, price: 10m, costPrice: 6m, Utc(5, 10));      // 40%
        await SaleAsync(organization, fell, 10m, price: 10m, costPrice: 4m, Utc(4, 10));      // 60%  -> -20 points
        await SaleAsync(organization, steady, 10m, price: 10m, costPrice: 5m, Utc(5, 10));    // 50%
        await SaleAsync(organization, steady, 10m, price: 10m, costPrice: 5.4m, Utc(4, 10));  // 46%  -> +4 points

        var summary = (await SummaryAsync(organization)).Value;

        Assert.Equal(["Loss maker"], summary.NegativeMargin.Select(p => p.Name).ToArray());
        Assert.Equal(-30m, summary.NegativeMargin[0].Current.GrossMargin);
        Assert.Equal(["Fell"], summary.DecliningMargin.Select(p => p.Name).ToArray());
        Assert.Equal(-20m, summary.DecliningMargin[0].MarginChangePoints);
        Assert.Equal(["Steady", "Fell", "Loss maker"], summary.TopProducts.Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task TheTopParameter_LimitsTheRankedProducts()
    {
        var organization = await NewOrganizationAsync();
        foreach (var (name, cost) in new[] { ("A", 2m), ("B", 4m), ("C", 6m) })
        {
            await SaleAsync(organization, await NewProductAsync(organization, name), 10m, price: 10m, costPrice: cost, Utc(5, 10));
        }

        var two = (await SummaryAsync(organization, top: 2)).Value;

        Assert.Equal(["A", "B"], two.TopProducts.Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task ThePeriodBoundaries_AreHalfOpen_InTheOrganizationsTimeZone()
    {
        // Tokyo is UTC+9: the current period starts 2026-05-01T15:00Z (inclusive) and ends 2026-05-31T15:00Z (exclusive).
        var organization = await NewOrganizationAsync("Asia/Tokyo");
        var rice = await NewProductAsync(organization, "Rice");
        await SaleAsync(organization, rice, 1m, price: 10m, costPrice: 5m, Utc(5, 1, 14, 59));
        await SaleAsync(organization, rice, 2m, price: 10m, costPrice: 5m, Utc(5, 1, 15, 0));
        await SaleAsync(organization, rice, 4m, price: 10m, costPrice: 5m, Utc(5, 31, 14, 59));
        await SaleAsync(organization, rice, 8m, price: 10m, costPrice: 5m, Utc(5, 31, 15, 0));

        var summary = (await SummaryAsync(organization)).Value;

        Assert.Equal((60m, 10m), (summary.Totals.Current.Revenue, summary.Totals.Previous.Revenue));
    }

    [Fact]
    public async Task TheMargin_ReconcilesWithTheStoredSaleLines_ToTheLastDecimal()
    {
        var organization = await NewOrganizationAsync();
        var oil = await NewProductAsync(organization, "Oil");
        var rice = await NewProductAsync(organization, "Rice");
        var free = await NewProductAsync(organization, "Free", costPrice: 0m);
        // Fractional quantities and prices make every line total and line cost need rounding.
        await SaleAsync(organization, oil, 0.3333m, price: 1.5m, costPrice: 0.9m, Utc(5, 3));
        await SaleAsync(organization, oil, 7.7777m, price: 0.0003m, costPrice: 0.0002m, Utc(5, 9));
        await SaleAsync(organization, rice, 2.5m, price: 3.3333m, costPrice: 1.7777m, Utc(5, 12));
        await SaleAsync(organization, rice, 11.1111m, price: 2.2222m, costPrice: 2.5555m, Utc(5, 31, 23, 59, 59)); // sold at a loss
        await SaleAsync(organization, rice, 9.9999m, price: 4.4444m, costPrice: 3.3333m, Utc(4, 5));
        await SaleAsync(organization, free, 5m, price: 7m, costPrice: 0m, Utc(5, 15));  // no recorded cost
        await SaleAsync(organization, oil, 100m, price: 9m, costPrice: 1m, Utc(5, 15), voided: true);
        var other = await NewOrganizationAsync();
        await SaleAsync(other, await NewProductAsync(other, "Theirs"), 1000m, price: 10m, costPrice: 1m, Utc(5, 15));

        var summary = (await SummaryAsync(organization)).Value;

        var current = await RawAsync(organization, Utc(5, 2, 0), Utc(6, 1, 0));
        var previous = await RawAsync(organization, Utc(4, 2, 0), Utc(5, 2, 0));
        Assert.Equal((current.Revenue, current.Cost), (summary.Totals.Current.Revenue, summary.Totals.Current.Cost));
        Assert.Equal((previous.Revenue, previous.Cost), (summary.Totals.Previous.Revenue, summary.Totals.Previous.Cost));
        Assert.NotEqual(0m, current.Revenue);
        Assert.Equal(current.Revenue - current.Cost, summary.Totals.Current.GrossMargin);
        Assert.Equal(35m, summary.Totals.Current.RevenueWithoutCost); // the line with no recorded cost, left out of the margin
        // The products add up to the total.
        Assert.Equal(summary.Totals.Current.GrossMargin, summary.TopProducts.Sum(p => p.Current.GrossMargin));
    }

    /// <summary>Revenue and cost of the costed lines of completed sales in the period, summed in SQL with the same per-line rounding.</summary>
    private async Task<(decimal Revenue, decimal Cost)> RawAsync(Guid organization, DateTimeOffset from, DateTimeOffset to)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COALESCE(SUM(ROUND(l.\"UnitPrice\" * l.\"Quantity\", 4)), 0), COALESCE(SUM(ROUND(l.\"CostPrice\" * l.\"Quantity\", 4)), 0) FROM \"SaleLines\" l " +
            "JOIN \"Sales\" s ON s.\"Id\" = l.\"SaleId\" AND s.\"OrganizationId\" = l.\"OrganizationId\" " +
            "WHERE s.\"OrganizationId\" = @org AND s.\"Status\" = 'Completed' AND l.\"CostPrice\" > 0 AND s.\"CreatedAtUtc\" >= @from AND s.\"CreatedAtUtc\" < @to";
        command.Parameters.AddWithValue("org", organization);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (Convert.ToDecimal(reader.GetValue(0), CultureInfo.InvariantCulture), Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task WithNoSales_TheSummaryIsEmpty_NotAnError()
    {
        var organization = await NewOrganizationAsync();

        var result = await SummaryAsync(organization);

        Assert.True(result.IsSuccess);
        Assert.Equal(("no_sales", null), (result.Value.Totals.Current.Status, result.Value.Totals.Current.MarginPercent));
        Assert.Null(result.Value.Totals.MarginChangePoints);
        Assert.Empty(result.Value.TopProducts);
        Assert.Empty(result.Value.NegativeMargin);
        Assert.Empty(result.Value.DecliningMargin);
        Assert.Empty(result.Value.MissingCost);
    }

    // ---- permissions, validation and isolation

    [Fact]
    public async Task AStaffMember_IsRefused_BecauseTheMarginRevealsCosts()
    {
        var organization = await NewOrganizationAsync();

        var summary = await SummaryAsync(organization, role: Role.Staff);
        var insights = await InsightsAsync(organization, Role.Staff);

        Assert.Equal(ErrorKind.Forbidden, summary.Error!.Kind);
        Assert.Equal(ErrorKind.Forbidden, insights.Error!.Kind);
    }

    [Fact]
    public async Task AManager_MaySeeTheMargins()
    {
        var organization = await NewOrganizationAsync();

        Assert.True((await SummaryAsync(organization, role: Role.Manager)).IsSuccess);
    }

    [Theory]
    [InlineData(0, null, "margin_summary.days_invalid", "days")]
    [InlineData(93, null, "margin_summary.days_invalid", "days")]
    [InlineData(30, 0, "margin_summary.top_invalid", "top")]
    [InlineData(30, 51, "margin_summary.top_invalid", "top")]
    public async Task InvalidParameters_AreRejected_WithTheFieldAndACode(int days, int? top, string code, string field)
    {
        var organization = await NewOrganizationAsync();

        var result = await SummaryAsync(organization, days, top);

        Assert.Equal((ErrorKind.Validation, code, field), (result.Error!.Kind, result.Error.Code, result.Error.Field));
    }

    [Fact]
    public async Task AnotherOrganizationsSales_NeverAppear()
    {
        var first = await NewOrganizationAsync();
        var second = await NewOrganizationAsync();
        await SaleAsync(first, await NewProductAsync(first, "Mine"), 2m, price: 10m, costPrice: 4m, Utc(5, 10));
        await SaleAsync(second, await NewProductAsync(second, "Theirs"), 900m, price: 10m, costPrice: 1m, Utc(5, 10));

        var mine = (await SummaryAsync(first)).Value;
        var theirs = (await SummaryAsync(second)).Value;

        Assert.Equal((20m, 8m), (mine.Totals.Current.Revenue, mine.Totals.Current.Cost));
        Assert.Equal(["Mine"], mine.TopProducts.Select(p => p.Name).ToArray());
        Assert.Equal(9000m, theirs.Totals.Current.Revenue);
    }

    // ---- the insights

    [Fact]
    public async Task ASaleBelowCost_IsAHighSeverityInsight_ShowingPriceCostAndMarginOfBothPeriods()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Rice");
        await SaleAsync(organization, rice, 10m, price: 5m, costPrice: 8m, Utc(5, 10));
        await SaleAsync(organization, rice, 10m, price: 10m, costPrice: 6m, Utc(4, 10));

        var insight = Assert.Single((await InsightsAsync(organization)).Value.Items);

        Assert.Equal(("margin.negative_margin", "High", "Rice"), (insight.Type, insight.Severity, insight.Subject.Name));
        Assert.Equal("review_product_pricing", insight.RecommendedAction.Code);
        Assert.Equal("50", insight.Facts.Single(f => f.Key == "revenue").Value);
        Assert.Equal("80", insight.Facts.Single(f => f.Key == "cost").Value);
        Assert.Equal("-30", insight.Facts.Single(f => f.Key == "gross_margin").Value);
        Assert.Equal("-60", insight.Facts.Single(f => f.Key == "margin_percent").Value);
        Assert.Equal("10", insight.Facts.Single(f => f.Key == "previous_average_unit_price").Value);
        Assert.Equal("6", insight.Facts.Single(f => f.Key == "previous_average_unit_cost").Value);
        Assert.Equal("40", insight.Facts.Single(f => f.Key == "previous_margin_percent").Value);
        Assert.Contains("sold at a loss", insight.Reason);
        Assert.Contains("Average price 10 to 5, average cost 6 to 8.", insight.Reason);
        Assert.Equal("2026-05-02 to 2026-05-31 (UTC)", insight.Facts.Single(f => f.Key == "period").Value);
    }

    [Fact]
    public async Task AMarginThatFellByFivePointsOrMore_IsAMediumInsight_AndASmallerFallIsNot()
    {
        var organization = await NewOrganizationAsync();
        var fell = await NewProductAsync(organization, "Fell");
        var slight = await NewProductAsync(organization, "Slight");
        await SaleAsync(organization, fell, 10m, price: 10m, costPrice: 6m, Utc(5, 10));      // 40%
        await SaleAsync(organization, fell, 10m, price: 10m, costPrice: 4.5m, Utc(4, 10));    // 55%  -> -15 points
        await SaleAsync(organization, slight, 10m, price: 10m, costPrice: 5.01m, Utc(5, 10)); // 49.9%
        await SaleAsync(organization, slight, 10m, price: 10m, costPrice: 4.6m, Utc(4, 10));  // 54%  -> -4.1 points

        var insight = Assert.Single((await InsightsAsync(organization)).Value.Items);

        Assert.Equal(("margin.margin_decline", "Medium", "Fell"), (insight.Type, insight.Severity, insight.Subject.Name));
        Assert.Equal("-15", insight.Facts.Single(f => f.Key == "margin_change_points").Value);
        Assert.StartsWith("The margin on Fell fell from 55% to 40% (15 points)", insight.Reason);
    }

    [Fact]
    public async Task AProductWithoutAMarginInEitherPeriod_ProducesNoInsight()
    {
        var organization = await NewOrganizationAsync();
        var uncosted = await NewProductAsync(organization, "Uncosted", costPrice: 0m);
        var onlyNow = await NewProductAsync(organization, "Only now");
        await SaleAsync(organization, uncosted, 10m, price: 10m, costPrice: 0m, Utc(5, 10));
        await SaleAsync(organization, uncosted, 10m, price: 10m, costPrice: 0m, Utc(4, 10));
        await SaleAsync(organization, onlyNow, 10m, price: 10m, costPrice: 9m, Utc(5, 10)); // thin but positive, and nothing to compare with

        Assert.Empty((await InsightsAsync(organization)).Value.Items);
    }

    [Fact]
    public async Task ALossMakerThatAlsoFell_IsReportedOnce_AsTheLoss()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization, "Rice");
        await SaleAsync(organization, rice, 10m, price: 5m, costPrice: 8m, Utc(5, 10));
        await SaleAsync(organization, rice, 10m, price: 10m, costPrice: 4m, Utc(4, 10));

        var insight = Assert.Single((await InsightsAsync(organization)).Value.Items);

        Assert.Equal("margin.negative_margin", insight.Type);
    }

    [Fact]
    public async Task TheInsights_AreReproducible_ForTheSameDataAndClock()
    {
        var organization = await NewOrganizationAsync();
        for (var i = 0; i < 6; i++)
        {
            var product = await NewProductAsync(organization, $"P{i}");
            await SaleAsync(organization, product, 10m, price: 10m, costPrice: 4m + i, Utc(5, 12));
            await SaleAsync(organization, product, 10m, price: 10m, costPrice: 3m, Utc(4, 12));
        }

        var first = (await InsightsAsync(organization)).Value.Items.Select(i => (i.Key, i.Severity, i.Reason)).ToArray();
        var second = (await InsightsAsync(organization)).Value.Items.Select(i => (i.Key, i.Severity, i.Reason)).ToArray();

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }
}
