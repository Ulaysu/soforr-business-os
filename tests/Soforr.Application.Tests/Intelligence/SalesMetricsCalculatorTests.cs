using Soforr.Application.Intelligence;
using Soforr.Application.Intelligence.Rules;

namespace Soforr.Application.Tests.Intelligence;

public class SalesMetricsCalculatorTests
{
    // 2026-06-01 10:00 UTC, 10-day periods in UTC: current 2026-05-22..2026-05-31, previous 2026-05-12..2026-05-21.
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly InsightWindow Current = InsightWindows.TrailingCompleteDays(Now, TimeZoneInfo.Utc, 10);
    private static readonly InsightWindow Previous = InsightWindows.Preceding(Current, TimeZoneInfo.Utc);
    private static readonly DateTimeOffset LongAgo = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CatalogProduct Product(string name, bool active = true, DateTimeOffset? created = null, Guid? id = null, string unit = "bag") =>
        new(id ?? Guid.CreateVersion7(), "SKU-" + name, name, unit, active, created ?? LongAgo);

    private static SoldLine Sold(CatalogProduct product, decimal quantity, decimal revenue, DateTimeOffset at, Guid? sale = null) =>
        new(sale ?? Guid.CreateVersion7(), product.ProductId, at, quantity, revenue);

    private static DateTimeOffset InCurrent(int day = 25) => new(2026, 5, day, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset InPrevious(int day = 15) => new(2026, 5, day, 12, 0, 0, TimeSpan.Zero);

    private static SalesMetrics Calculate(SoldLine[] lines, CatalogProduct[] products, int top = 10) =>
        SalesMetricsCalculator.Calculate(lines, products, Current, Previous, top);

    // ---- the periods

    [Fact]
    public void ThePeriods_AreAdjacent_AndOfEqualLength()
    {
        Assert.Equal(new DateOnly(2026, 5, 22), Current.FirstDay);
        Assert.Equal(new DateOnly(2026, 5, 31), Current.LastDay);
        Assert.Equal(new DateOnly(2026, 5, 12), Previous.FirstDay);
        Assert.Equal(new DateOnly(2026, 5, 21), Previous.LastDay);
        Assert.Equal(Previous.EndUtc, Current.StartUtc);
        Assert.Equal(Current.Days, Previous.Days);
    }

    [Fact]
    public void ALine_BelongsToThePeriodContainingItsTime_WithHalfOpenBoundaries()
    {
        var rice = Product("Rice");
        var lines = new[]
        {
            Sold(rice, 1m, 10m, Previous.StartUtc.AddSeconds(-1)),  // before both periods: counted in neither
            Sold(rice, 2m, 20m, Previous.StartUtc),                 // first instant of the previous period
            Sold(rice, 4m, 40m, Previous.EndUtc.AddSeconds(-1)),    // last instant of the previous period
            Sold(rice, 8m, 80m, Current.StartUtc),                  // first instant of the current period
            Sold(rice, 16m, 160m, Current.EndUtc.AddSeconds(-1)),   // last instant of the current period
            Sold(rice, 32m, 320m, Current.EndUtc),                  // today: counted in neither
        };

        var metrics = Calculate(lines, [rice]);

        Assert.Equal((240m, 60m), (metrics.Totals.Revenue, metrics.Totals.PreviousRevenue));
        var sales = Assert.Single(metrics.TopSellers);
        Assert.Equal((24m, 6m), (sales.Quantity, sales.PreviousQuantity));
    }

    // ---- revenue and growth

    [Fact]
    public void RevenueGrowth_IsTheChangeOverThePreviousRevenue_AsAPercentage_ToOneDecimal()
    {
        var rice = Product("Rice");
        var lines = new[] { Sold(rice, 1m, 120m, InCurrent()), Sold(rice, 1m, 100m, InPrevious()) };

        var totals = Calculate(lines, [rice]).Totals;

        Assert.Equal(120m, totals.Revenue);
        Assert.Equal(100m, totals.PreviousRevenue);
        Assert.Equal(20m, totals.RevenueGrowthPercent);
    }

    [Fact]
    public void RevenueGrowth_CanBeNegative_AndIsRoundedHalfAwayFromZero()
    {
        var rice = Product("Rice");

        var down = Calculate([Sold(rice, 1m, 90m, InCurrent()), Sold(rice, 1m, 100m, InPrevious())], [rice]).Totals;
        var thirds = Calculate([Sold(rice, 1m, 100m, InCurrent()), Sold(rice, 1m, 3m, InPrevious())], [rice]).Totals; // +3233.333...%
        var half = Calculate([Sold(rice, 1m, 100.25m, InCurrent()), Sold(rice, 1m, 100m, InPrevious())], [rice]).Totals; // +0.25% -> 0.3

        Assert.Equal(-10m, down.RevenueGrowthPercent);
        Assert.Equal(3233.3m, thirds.RevenueGrowthPercent);
        Assert.Equal(0.3m, half.RevenueGrowthPercent);
    }

    [Fact]
    public void WithNoPreviousRevenue_ThereIsNoGrowthFigure_NotZeroOrInfinity()
    {
        var rice = Product("Rice");

        var onlyCurrent = Calculate([Sold(rice, 1m, 100m, InCurrent())], [rice]).Totals;
        var nothing = Calculate([], [rice]).Totals;

        Assert.Null(onlyCurrent.RevenueGrowthPercent);
        Assert.Equal(100m, onlyCurrent.Revenue);
        Assert.Null(nothing.RevenueGrowthPercent);
        Assert.Equal((0m, 0m, 0, 0), (nothing.Revenue, nothing.PreviousRevenue, nothing.SaleCount, nothing.PreviousSaleCount));
    }

    [Fact]
    public void ACurrentPeriodWithNoSales_AgainstAPreviousOne_IsAMinusHundredPercent()
    {
        var rice = Product("Rice");

        var totals = Calculate([Sold(rice, 1m, 100m, InPrevious())], [rice]).Totals;

        Assert.Equal(-100m, totals.RevenueGrowthPercent);
    }

    [Fact]
    public void TheSaleCount_IsTheNumberOfDistinctSales_NotLines()
    {
        var rice = Product("Rice");
        var oil = Product("Oil");
        var sale = Guid.CreateVersion7();
        var lines = new[]
        {
            Sold(rice, 1m, 10m, InCurrent(), sale),
            Sold(oil, 1m, 10m, InCurrent(), sale),
            Sold(rice, 1m, 10m, InCurrent(26)),
            Sold(rice, 1m, 10m, InPrevious()),
        };

        var totals = Calculate(lines, [rice, oil]).Totals;

        Assert.Equal((2, 1), (totals.SaleCount, totals.PreviousSaleCount));
        Assert.Equal(30m, totals.Revenue);
    }

    // ---- top sellers

    [Fact]
    public void TopSellers_AreOrderedByQuantity_ThenRevenue_ThenName_ThenId_AndLimited()
    {
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var a = Product("A");
        var b = Product("B");
        var c = Product("C");
        var same1 = Product("Same", id: secondId);
        var same2 = Product("Same", id: firstId);
        var never = Product("Never");
        var lines = new[]
        {
            Sold(a, 5m, 50m, InCurrent()),
            Sold(b, 9m, 10m, InCurrent()),
            Sold(c, 5m, 70m, InCurrent()),   // same quantity as A, more revenue: first
            Sold(same1, 2m, 20m, InCurrent()),
            Sold(same2, 2m, 20m, InCurrent()),
        };

        var all = Calculate(lines, [a, b, c, same1, same2, never]).TopSellers;
        var two = Calculate(lines, [a, b, c, same1, same2, never], top: 2).TopSellers;

        Assert.Equal(["B", "C", "A", "Same", "Same"], all.Select(s => s.Product.Name).ToArray());
        Assert.Equal([firstId, secondId], all.Where(s => s.Product.Name == "Same").Select(s => s.Product.ProductId).ToArray());
        Assert.Equal(["B", "C"], two.Select(s => s.Product.Name).ToArray());
        Assert.DoesNotContain(all, s => s.Product.Name == "Never"); // sold nothing in the current period
    }

    [Fact]
    public void TopSellers_SumTheLinesOfAProduct_AndCarryThePreviousPeriod()
    {
        var rice = Product("Rice");
        var lines = new[]
        {
            Sold(rice, 3m, 30m, InCurrent(23)),
            Sold(rice, 4m, 44m, InCurrent(27)),
            Sold(rice, 5m, 50m, InPrevious(13)),
        };

        var sales = Assert.Single(Calculate(lines, [rice]).TopSellers);

        Assert.Equal((7m, 74m, 5m, 50m), (sales.Quantity, sales.Revenue, sales.PreviousQuantity, sales.PreviousRevenue));
    }

    [Fact]
    public void QuantityIsNeverSummedAcrossProducts_ButRevenueIs()
    {
        var bag = Product("Rice", unit: "bag");
        var kilo = Product("Sugar", unit: "kg");

        var metrics = Calculate([Sold(bag, 2m, 20m, InCurrent()), Sold(kilo, 3.5m, 35m, InCurrent())], [bag, kilo]);

        Assert.Equal(55m, metrics.Totals.Revenue);
        Assert.Equal(["Sugar", "Rice"], metrics.TopSellers.Select(s => s.Product.Name).ToArray());
        Assert.Equal([3.5m, 2m], metrics.TopSellers.Select(s => s.Quantity).ToArray());
    }

    // ---- declining products

    [Theory]
    [InlineData(100, 80, true)]   // exactly 20% down: included
    [InlineData(100, 80.0001, false)]
    [InlineData(100, 79, true)]
    [InlineData(100, 100, false)]
    [InlineData(100, 150, false)]
    [InlineData(10, 8, true)]
    public void AProductIsDeclining_WhenItsQuantityFellByAtLeastTwentyPercent(double previous, double current, bool declining)
    {
        var rice = Product("Rice");
        var lines = new[] { Sold(rice, (decimal)current, 1m, InCurrent()), Sold(rice, (decimal)previous, 1m, InPrevious()) };

        var result = Calculate(lines, [rice]).DecliningProducts;

        Assert.Equal(declining, result.Count == 1);
    }

    [Fact]
    public void ADecliningProduct_NeedsSalesInBothPeriods()
    {
        var onlyBefore = Product("Stopped");
        var onlyNow = Product("New");
        var lines = new[] { Sold(onlyBefore, 100m, 1m, InPrevious()), Sold(onlyNow, 100m, 1m, InCurrent()) };

        Assert.Empty(Calculate(lines, [onlyBefore, onlyNow]).DecliningProducts);
    }

    [Fact]
    public void Declining_ShowsThePercentageToOneDecimal_AndIsOrderedLargestDeclineFirst()
    {
        var small = Product("Small");
        var big = Product("Big");
        var third = Product("Third");
        var lines = new[]
        {
            Sold(small, 70m, 1m, InCurrent()), Sold(small, 100m, 1m, InPrevious()),  // -30%
            Sold(big, 10m, 1m, InCurrent()), Sold(big, 100m, 1m, InPrevious()),      // -90%
            Sold(third, 2m, 1m, InCurrent()), Sold(third, 3m, 1m, InPrevious()),     // -33.333...% -> 33.3
        };

        var declining = Calculate(lines, [small, big, third]).DecliningProducts;

        Assert.Equal(["Big", "Third", "Small"], declining.Select(d => d.Sales.Product.Name).ToArray());
        Assert.Equal([90m, 33.3m, 30m], declining.Select(d => d.DeclinePercent).ToArray());
    }

    // ---- slow movers

    private static CatalogProduct[] Catalog(int count, DateTimeOffset? created = null) =>
        Enumerable.Range(0, count).Select(i => Product($"P{i:D2}", created: created)).ToArray();

    [Fact]
    public void WithFewerThanFiveQualifyingProducts_TheWholeOrderedListIsShown()
    {
        var products = Catalog(4);
        var lines = new[] { Sold(products[2], 7m, 1m, InCurrent()), Sold(products[0], 3m, 1m, InCurrent()) };

        var slow = Calculate(lines, products).SlowMovers;

        Assert.True(slow.ShowsFullList);
        Assert.Equal(4, slow.QualifyingProducts);
        Assert.Equal(["P01", "P03", "P00", "P02"], slow.Items.Select(i => i.Product.Name).ToArray()); // 0, 0, 3, 7 (ties by name)
    }

    [Theory]
    [InlineData(5, 1)]
    [InlineData(6, 1)]
    [InlineData(9, 1)]
    [InlineData(10, 2)]
    [InlineData(14, 2)]
    [InlineData(15, 3)]
    [InlineData(100, 20)]
    public void WithAtLeastFiveQualifyingProducts_TheBottomTwentyPercentAreShown_AtLeastOne(int qualifying, int expectedShown)
    {
        var products = Catalog(qualifying);
        // Product i sold i units, so the slowest are P00, P01, ...
        var lines = products.Select((product, i) => Sold(product, i + 1m, 1m, InCurrent())).ToArray();

        var slow = Calculate(lines, products).SlowMovers;

        Assert.False(slow.ShowsFullList);
        Assert.Equal(qualifying, slow.QualifyingProducts);
        Assert.Equal(products.Take(expectedShown).Select(p => p.Name), slow.Items.Select(i => i.Product.Name));
    }

    [Fact]
    public void AProductThatSoldNothing_IsTheSlowest_WithZero()
    {
        var products = Catalog(6);
        var lines = products.Skip(1).Select(product => Sold(product, 5m, 1m, InCurrent())).ToArray();

        var slow = Calculate(lines, products).SlowMovers;

        var item = Assert.Single(slow.Items);
        Assert.Equal("P00", item.Product.Name);
        Assert.Equal(0m, item.Quantity);
    }

    [Fact]
    public void OnlyActiveProducts_ThatExistedForTheWholeCurrentPeriod_Qualify()
    {
        var old = Catalog(5);
        var inactive = Product("Inactive", active: false);
        var tooNew = Product("TooNew", created: Current.StartUtc.AddSeconds(1));
        var justInTime = Product("JustInTime", created: Current.StartUtc.AddSeconds(-1));

        var slow = Calculate([], [.. old, inactive, tooNew, justInTime]).SlowMovers;

        Assert.Equal(6, slow.QualifyingProducts); // the five old products and JustInTime
        Assert.DoesNotContain(slow.Items, item => item.Product.Name is "Inactive" or "TooNew");
    }

    [Fact]
    public void ProductCreatedExactlyWhenThePeriodStarts_DoesNotQualify()
    {
        var atStart = Product("AtStart", created: Current.StartUtc);

        var slow = Calculate([], [atStart]).SlowMovers;

        Assert.Equal(0, slow.QualifyingProducts);
    }

    [Fact]
    public void SlowMovers_AreOrderedByQuantityThenNameThenId()
    {
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var products = new[] { Product("B"), Product("A"), Product("Same", id: secondId), Product("Same", id: firstId), Product("Z") };

        var slow = Calculate([], products).SlowMovers;

        Assert.False(slow.ShowsFullList);
        var item = Assert.Single(slow.Items);
        Assert.Equal("A", item.Product.Name);
        Assert.Equal(5, slow.QualifyingProducts);
        var full = Calculate([], products.Take(4).ToArray()).SlowMovers;
        Assert.Equal(["A", "B", "Same", "Same"], full.Items.Select(i => i.Product.Name).ToArray());
        Assert.Equal([firstId, secondId], full.Items.Where(i => i.Product.Name == "Same").Select(i => i.Product.ProductId).ToArray());
    }

    // ---- determinism and no data

    [Fact]
    public void TheSameInput_GivesTheSameResult_WhateverTheInputOrder()
    {
        var products = Catalog(12);
        var lines = products.SelectMany((product, i) => new[] { Sold(product, i + 1m, (i + 1) * 10m, InCurrent(22 + i % 9)), Sold(product, 12m - i, 5m, InPrevious(12 + i % 9)) }).ToArray();

        static string[] Describe(SalesMetrics m) =>
        [
            string.Join(",", m.TopSellers.Select(s => s.Product.Name)),
            string.Join(",", m.SlowMovers.Items.Select(s => s.Product.Name)),
            string.Join(",", m.DecliningProducts.Select(d => d.Sales.Product.Name + d.DeclinePercent)),
            m.Totals.Revenue + "/" + m.Totals.PreviousRevenue + "/" + m.Totals.RevenueGrowthPercent,
        ];

        var forward = Describe(Calculate(lines, products));
        var again = Describe(Calculate(lines, products));
        var reversed = Describe(Calculate(lines.Reverse().ToArray(), products.Reverse().ToArray()));

        Assert.Equal(forward, again);
        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void WithNoDataAtAll_EverythingIsEmpty_NotAnError()
    {
        var metrics = Calculate([], []);

        Assert.Empty(metrics.TopSellers);
        Assert.Empty(metrics.DecliningProducts);
        Assert.True(metrics.SlowMovers.ShowsFullList);
        Assert.Empty(metrics.SlowMovers.Items);
        Assert.Null(metrics.Totals.RevenueGrowthPercent);
    }

    [Fact]
    public void ATopCountBelowOne_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Calculate([], [], top: 0));
    }
}
