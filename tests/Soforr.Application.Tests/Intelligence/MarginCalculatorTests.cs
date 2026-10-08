using Soforr.Application.Intelligence;
using Soforr.Application.Intelligence.Rules;

namespace Soforr.Application.Tests.Intelligence;

public class MarginCalculatorTests
{
    // 2026-06-01 10:00 UTC, 10-day periods in UTC: current 2026-05-22..2026-05-31, previous 2026-05-12..2026-05-21.
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly InsightWindow Current = InsightWindows.TrailingCompleteDays(Now, TimeZoneInfo.Utc, 10);
    private static readonly InsightWindow Previous = InsightWindows.Preceding(Current, TimeZoneInfo.Utc);
    private static readonly DateTimeOffset LongAgo = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CatalogProduct Product(string name, Guid? id = null) =>
        new(id ?? Guid.CreateVersion7(), "SKU-" + name, name, "bag", true, LongAgo);

    /// <summary>A costed line: the cost price was recorded on the sale.</summary>
    private static SoldLine Sold(CatalogProduct product, decimal quantity, decimal revenue, decimal cost, DateTimeOffset at, Guid? sale = null) =>
        new(sale ?? Guid.CreateVersion7(), product.ProductId, at, quantity, revenue, cost, CostRecorded: true);

    /// <summary>A line whose cost price was 0: no cost was recorded.</summary>
    private static SoldLine SoldWithoutCost(CatalogProduct product, decimal quantity, decimal revenue, DateTimeOffset at) =>
        new(Guid.CreateVersion7(), product.ProductId, at, quantity, revenue, 0m, CostRecorded: false);

    private static DateTimeOffset InCurrent(int day = 25) => new(2026, 5, day, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset InPrevious(int day = 15) => new(2026, 5, day, 12, 0, 0, TimeSpan.Zero);

    private static MarginMetrics Calculate(SoldLine[] lines, params CatalogProduct[] products) =>
        MarginCalculator.Calculate(lines, products, Current, Previous);

    // ---- the formulas

    [Fact]
    public void GrossMargin_IsRevenueMinusTheRecordedCost_AndThePercentageIsOverRevenue()
    {
        var rice = Product("Rice");

        var margin = Assert.Single(Calculate([Sold(rice, 10m, 100m, 60m, InCurrent())], rice).Products);

        Assert.Equal((100m, 60m, 40m, 40m), (margin.Current.Revenue, margin.Current.Cost, margin.Current.GrossMargin, margin.Current.MarginPercent));
        Assert.Equal(MarginStatus.Ok, margin.Current.Status);
    }

    [Theory]
    [InlineData(100, 66.6667)]    // 33.3333% -> 33.3
    [InlineData(100, 33.3333)]    // 66.6667% -> 66.7
    [InlineData(10000, 5495)]     // 45.05% -> 45.1 (half away from zero)
    [InlineData(10000, 6004.5)]   // 39.955% -> 40.0
    public void ThePercentage_IsRoundedToOneDecimal_HalfAwayFromZero(double revenue, double cost)
    {
        var rice = Product("Rice");
        var exact = ((decimal)revenue - (decimal)cost) / (decimal)revenue * 100m;

        var margin = Assert.Single(Calculate([Sold(rice, 1m, (decimal)revenue, (decimal)cost, InCurrent())], rice).Products);

        Assert.Equal(decimal.Round(exact, 1, MidpointRounding.AwayFromZero), margin.Current.MarginPercent);
    }

    [Fact]
    public void AHalfwayPercentage_RoundsAwayFromZero_NotToEven()
    {
        var rice = Product("Rice");

        var margin = Assert.Single(Calculate([Sold(rice, 1m, 10000m, 5495m, InCurrent())], rice).Products);

        Assert.Equal(45.1m, margin.Current.MarginPercent); // exactly 45.05 -> 45.1, where banker's rounding would give 45.0
    }

    [Fact]
    public void ASaleBelowCost_HasANegativeMargin_AndANegativePercentage()
    {
        var rice = Product("Rice");

        var margin = Assert.Single(Calculate([Sold(rice, 10m, 80m, 100m, InCurrent())], rice).Products);

        Assert.Equal((-20m, -25m), (margin.Current.GrossMargin, margin.Current.MarginPercent));
    }

    [Fact]
    public void WithNoRevenue_ThereIsNoPercentage_ButAGiveawayStillShowsItsCost()
    {
        var rice = Product("Rice");

        var margin = Assert.Single(Calculate([Sold(rice, 5m, 0m, 30m, InCurrent())], rice).Products);

        Assert.Null(margin.Current.MarginPercent);
        Assert.Equal(-30m, margin.Current.GrossMargin);
        Assert.Equal(MarginStatus.Ok, margin.Current.Status);
    }

    [Fact]
    public void TheAverages_AreRevenueAndCostPerUnit_OverTheCostedLines_ToFourDecimals()
    {
        var rice = Product("Rice");
        var lines = new[] { Sold(rice, 3m, 10m, 6m, InCurrent(23)), Sold(rice, 4m, 15m, 8m, InCurrent(27)) };

        var margin = Assert.Single(Calculate(lines, rice).Products);

        Assert.Equal((7m, 25m, 14m), (margin.Current.Quantity, margin.Current.Revenue, margin.Current.Cost));
        Assert.Equal(3.5714m, margin.Current.AverageUnitPrice); // 25 / 7 = 3.571428...
        Assert.Equal(2m, margin.Current.AverageUnitCost);
    }

    // ---- the change in percentage points

    [Fact]
    public void TheChange_IsInPercentagePoints_CurrentMinusPrevious()
    {
        var rice = Product("Rice");
        var lines = new[] { Sold(rice, 1m, 100m, 60m, InCurrent()), Sold(rice, 1m, 100m, 50m, InPrevious()) };

        var margin = Assert.Single(Calculate(lines, rice).Products);

        Assert.Equal((40m, 50m, -10m), (margin.Current.MarginPercent, margin.Previous.MarginPercent, margin.MarginChangePoints));
    }

    [Fact]
    public void TheChange_IsComputedFromTheUnroundedPercentages_NotFromTheRoundedOnes()
    {
        // Previous 44.95% (shown 45.0) and current 40.04% (shown 40.0): the rounded figures differ by 5.0 points, the real
        // change is 4.91 points, so the margin has NOT fallen by 5 points.
        var rice = Product("Rice");
        var lines = new[] { Sold(rice, 1m, 10000m, 5996m, InCurrent()), Sold(rice, 1m, 10000m, 5505m, InPrevious()) };

        var margin = Assert.Single(Calculate(lines, rice).Products);

        Assert.Equal((40.0m, 45.0m), (margin.Current.MarginPercent, margin.Previous.MarginPercent));
        Assert.Equal(-4.9m, margin.MarginChangePoints);
        Assert.False(MarginCalculator.HasDeclined(margin));
    }

    [Theory]
    [InlineData(5495, 6000, true)]   // 45.05% -> 40.00%: -5.05 points
    [InlineData(5500, 6000, true)]   // 45.00% -> 40.00%: exactly -5.0 points
    [InlineData(5500, 5999, false)]  // 45.00% -> 40.01%: -4.99 points
    [InlineData(5500, 5500, false)]
    public void AMarginHasDeclined_WhenItFellByAtLeastFivePoints(int previousCost, int currentCost, bool declined)
    {
        var rice = Product("Rice");
        var lines = new[] { Sold(rice, 1m, 10000m, currentCost, InCurrent()), Sold(rice, 1m, 10000m, previousCost, InPrevious()) };

        var margin = Assert.Single(Calculate(lines, rice).Products);

        Assert.Equal(declined, MarginCalculator.HasDeclined(margin));
    }

    [Fact]
    public void AnImprovedMargin_IsNeverADecline()
    {
        var rice = Product("Rice");
        var lines = new[] { Sold(rice, 1m, 100m, 40m, InCurrent()), Sold(rice, 1m, 100m, 80m, InPrevious()) };

        var margin = Assert.Single(Calculate(lines, rice).Products);

        Assert.Equal(40m, margin.MarginChangePoints);
        Assert.False(MarginCalculator.HasDeclined(margin));
    }

    [Fact]
    public void ThereIsNoChange_UnlessBothPeriodsHaveAPercentage()
    {
        var onlyNow = Product("Only now");
        var onlyBefore = Product("Only before");
        var lines = new[] { Sold(onlyNow, 1m, 100m, 60m, InCurrent()), Sold(onlyBefore, 1m, 100m, 60m, InPrevious()) };

        var metrics = Calculate(lines, onlyNow, onlyBefore);

        Assert.All(metrics.Products, margin =>
        {
            Assert.Null(margin.MarginChangePoints);
            Assert.False(MarginCalculator.HasDeclined(margin));
        });
        Assert.Equal(MarginStatus.NoSales, metrics.Products.Single(m => m.Product.Name == "Only now").Previous.Status);
        Assert.Equal(MarginStatus.NoSales, metrics.Products.Single(m => m.Product.Name == "Only before").Current.Status);
    }

    // ---- missing data is never guessed

    [Fact]
    public void SalesWithNoRecordedCost_HaveNoMargin_AndAreReportedAsRevenueWithoutCost()
    {
        var rice = Product("Rice");

        var margin = Assert.Single(Calculate([SoldWithoutCost(rice, 10m, 100m, InCurrent())], rice).Products);

        Assert.Equal(MarginStatus.CostNotRecorded, margin.Current.Status);
        Assert.Equal((0m, 0m, 0m), (margin.Current.Revenue, margin.Current.Cost, margin.Current.GrossMargin));
        Assert.Null(margin.Current.MarginPercent);
        Assert.Null(margin.Current.AverageUnitPrice);
        Assert.Equal((100m, 10m), (margin.Current.RevenueWithoutCost, margin.Current.QuantityWithoutCost));
    }

    [Fact]
    public void ALineWithoutACost_DoesNotFlatterTheMargin_OfAProductThatHasOtherCostedLines()
    {
        var rice = Product("Rice");
        var lines = new[] { Sold(rice, 10m, 100m, 60m, InCurrent(23)), SoldWithoutCost(rice, 10m, 100m, InCurrent(27)) };

        var margin = Assert.Single(Calculate(lines, rice).Products);

        // The margin is that of the costed line (40%); counting the uncosted 100 as pure profit would have said 70%.
        Assert.Equal(MarginStatus.PartlyCosted, margin.Current.Status);
        Assert.Equal((100m, 60m, 40m, 40m), (margin.Current.Revenue, margin.Current.Cost, margin.Current.GrossMargin, margin.Current.MarginPercent));
        Assert.Equal((100m, 10m), (margin.Current.RevenueWithoutCost, margin.Current.QuantityWithoutCost));
    }

    [Fact]
    public void TheTotals_AreOverAllSales_WithTheSameTreatmentOfMissingCosts()
    {
        var rice = Product("Rice");
        var oil = Product("Oil");
        var lines = new[]
        {
            Sold(rice, 1m, 100m, 60m, InCurrent()),
            Sold(oil, 1m, 200m, 100m, InCurrent()),
            SoldWithoutCost(oil, 1m, 500m, InCurrent()),
            Sold(rice, 1m, 100m, 50m, InPrevious()),
            Sold(oil, 1m, 100m, 50m, InPrevious()),
        };

        var totals = Calculate(lines, rice, oil).Totals;

        Assert.Equal((300m, 160m, 140m), (totals.Current.Revenue, totals.Current.Cost, totals.Current.GrossMargin));
        Assert.Equal(46.7m, totals.Current.MarginPercent); // 140 / 300
        Assert.Equal(500m, totals.Current.RevenueWithoutCost);
        Assert.Equal(MarginStatus.PartlyCosted, totals.Current.Status);
        Assert.Equal((200m, 100m, 50m), (totals.Previous.Revenue, totals.Previous.Cost, totals.Previous.MarginPercent));
        Assert.Equal(-3.3m, totals.MarginChangePoints); // 46.666... - 50
    }

    [Fact]
    public void WithNoSalesAtAll_EverythingIsEmpty_NotAnError()
    {
        var metrics = Calculate([], Product("Rice"));

        Assert.Empty(metrics.Products);
        Assert.Equal(MarginStatus.NoSales, metrics.Totals.Current.Status);
        Assert.Null(metrics.Totals.Current.MarginPercent);
        Assert.Null(metrics.Totals.MarginChangePoints);
    }

    // ---- periods, ordering, determinism

    [Fact]
    public void ALine_BelongsToThePeriodContainingItsTime_WithHalfOpenBoundaries()
    {
        var rice = Product("Rice");
        var lines = new[]
        {
            Sold(rice, 1m, 1m, 0.5m, Previous.StartUtc.AddSeconds(-1)),
            Sold(rice, 1m, 2m, 1m, Previous.StartUtc),
            Sold(rice, 1m, 4m, 2m, Previous.EndUtc.AddSeconds(-1)),
            Sold(rice, 1m, 8m, 4m, Current.StartUtc),
            Sold(rice, 1m, 16m, 8m, Current.EndUtc.AddSeconds(-1)),
            Sold(rice, 1m, 32m, 16m, Current.EndUtc),
        };

        var margin = Assert.Single(Calculate(lines, rice).Products);

        Assert.Equal((24m, 12m), (margin.Current.Revenue, margin.Current.Cost));
        Assert.Equal((6m, 3m), (margin.Previous.Revenue, margin.Previous.Cost));
    }

    [Fact]
    public void Products_AreListedOnlyWhenTheySold_OrderedByTheCurrentGrossMargin_ThenNameThenId()
    {
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var big = Product("Big");
        var small = Product("Small");
        var tieB = Product("Tie", secondId);
        var tieA = Product("Tie", firstId);
        var loss = Product("Loss");
        var never = Product("Never");
        var lines = new[]
        {
            Sold(big, 1m, 100m, 20m, InCurrent()),
            Sold(small, 1m, 100m, 90m, InCurrent()),
            Sold(tieB, 1m, 100m, 70m, InCurrent()),
            Sold(tieA, 1m, 100m, 70m, InCurrent()),
            Sold(loss, 1m, 10m, 50m, InCurrent()),
        };

        var names = Calculate(lines, big, small, tieB, tieA, loss, never).Products.Select(m => (m.Product.Name, m.Product.ProductId)).ToArray();

        Assert.Equal(["Big", "Tie", "Tie", "Small", "Loss"], names.Select(n => n.Name).ToArray());
        Assert.Equal([firstId, secondId], names.Where(n => n.Name == "Tie").Select(n => n.ProductId).ToArray());
    }

    [Fact]
    public void TheSameInput_GivesTheSameResult_WhateverTheInputOrder()
    {
        var products = Enumerable.Range(0, 8).Select(i => Product("P" + i)).ToArray();
        var lines = products.SelectMany((product, i) => new[]
        {
            Sold(product, i + 1m, (i + 1) * 20m, (i + 1) * 12m, InCurrent(22 + i)),
            Sold(product, i + 2m, (i + 2) * 20m, (i + 2) * 9m, InPrevious(12 + i)),
        }).ToArray();

        static string[] Describe(MarginMetrics m) =>
        [
            string.Join(",", m.Products.Select(p => p.Product.Name + ":" + p.Current.MarginPercent + ":" + p.MarginChangePoints)),
            m.Totals.Current.MarginPercent + "/" + m.Totals.Previous.MarginPercent + "/" + m.Totals.MarginChangePoints,
        ];

        var forward = Describe(Calculate(lines, products));
        var reversed = Describe(Calculate(lines.Reverse().ToArray(), products.Reverse().ToArray()));

        Assert.Equal(forward, reversed);
    }
}
