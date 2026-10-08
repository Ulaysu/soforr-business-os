using Soforr.Application.Intelligence;
using Soforr.Application.Intelligence.Rules;

namespace Soforr.Application.Tests.Intelligence;

public class PurchaseCostTests
{
    // Noon UTC on 2026-06-01; the recent window is the 30 whole days 2026-05-02 .. 2026-05-31.
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly InsightWindow Recent = InsightWindows.TrailingCompleteDays(Now, TimeZoneInfo.Utc, 30);
    private static readonly Guid Rice = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Oil = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid Acme = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Bravo = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly Guid Cargo = Guid.Parse("00000000-0000-0000-0000-0000000000b3");
    private static readonly PurchaseSupplier[] Suppliers = [new(Acme, "Acme"), new(Bravo, "Bravo"), new(Cargo, "Cargo")];
    private static readonly CatalogProduct[] Products =
    [
        new(Rice, "RICE", "Rice", "bag", true, Now.AddYears(-1)),
        new(Oil, "OIL", "Oil", "litre", true, Now.AddYears(-1)),
    ];

    private static PurchasedLine Line(Guid supplier, decimal cost, DateTimeOffset placed, decimal quantity = 10m, Guid? product = null, Guid? order = null) =>
        new(order ?? Guid.CreateVersion7(), product ?? Rice, supplier, placed, quantity, cost, cost * quantity);

    private static DateTimeOffset Day(int month, int day) => new(2026, month, day, 9, 0, 0, TimeSpan.Zero);

    private static PurchaseCosts Calculate(params PurchasedLine[] lines) => PurchaseCostCalculator.Calculate(lines, Suppliers, Products, Recent);

    private static ProductPurchaseCost Rices(params PurchasedLine[] lines) => Assert.Single(Calculate(lines).Products);

    // ---- the change

    [Fact]
    public void TheLatestCost_IsComparedWithTheSuppliersImmediatelyPreviousPurchase()
    {
        var cost = Rices(Line(Acme, 100m, Day(3, 1)), Line(Acme, 110m, Day(4, 1)), Line(Acme, 125m, Day(5, 10)));

        var supplier = Assert.Single(cost.Suppliers);
        Assert.Equal((125m, 110m, 13.6m), (supplier.Latest.UnitCost, supplier.Previous!.UnitCost, supplier.ChangePercent));
        Assert.Equal((Day(5, 10), Day(4, 1)), (supplier.Latest.PlacedAtUtc, supplier.Previous.PlacedAtUtc));
        Assert.True(supplier.Notable);
    }

    [Theory]
    [InlineData(100, 110, true)]      // exactly +10%
    [InlineData(100, 109.99, false)]
    [InlineData(100, 90, true)]       // exactly -10%
    [InlineData(100, 90.01, false)]
    [InlineData(100, 100, false)]
    [InlineData(3, 3.3, true)]        // exact decimal arithmetic, no floating point error
    public void ANotableChange_IsAtLeastTenPercentOfThePreviousCost_ExactlyAndInEitherDirection(double previous, double latest, bool notable)
    {
        var supplier = Assert.Single(Rices(Line(Acme, (decimal)previous, Day(4, 1)), Line(Acme, (decimal)latest, Day(5, 10))).Suppliers);

        Assert.Equal(notable, supplier.Notable);
    }

    [Fact]
    public void ThePercent_IsRoundedToOneDecimalPlace_HalfAwayFromZero_ButNotabilityUsesTheExactValue()
    {
        var up = Assert.Single(Rices(Line(Acme, 8m, Day(4, 1)), Line(Acme, 8.01m, Day(5, 1))).Suppliers);   // +0.125%
        var down = Assert.Single(Rices(Line(Acme, 8m, Day(4, 1)), Line(Acme, 7.99m, Day(5, 1))).Suppliers); // -0.125%
        var justUnder = Assert.Single(Rices(Line(Acme, 1000m, Day(4, 1)), Line(Acme, 1099.99m, Day(5, 1))).Suppliers); // +9.999%

        Assert.Equal((0.1m, -0.1m), (up.ChangePercent, down.ChangePercent));
        Assert.Equal((10.0m, false), (justUnder.ChangePercent, justUnder.Notable)); // shows 10.0 but is under 10%
    }

    [Fact]
    public void ASinglePurchase_HasNoChange_ItIsInsufficientHistory()
    {
        var supplier = Assert.Single(Rices(Line(Acme, 100m, Day(5, 10))).Suppliers);

        Assert.Null(supplier.Previous);
        Assert.Null(supplier.ChangePercent);
        Assert.False(supplier.Notable);
    }

    [Fact]
    public void ThePreviousPurchase_MustBeFromTheSameSupplier()
    {
        var cost = Rices(Line(Acme, 100m, Day(4, 1)), Line(Bravo, 200m, Day(5, 1)));

        Assert.All(cost.Suppliers, supplier => Assert.Null(supplier.Previous));
    }

    [Fact]
    public void PurchasesWithNoRecordedCost_AreLeftOutOfTheComparison_AndCounted()
    {
        var cost = Rices(Line(Acme, 100m, Day(3, 1)), Line(Acme, 0m, Day(4, 1)), Line(Acme, 120m, Day(5, 1)), Line(Bravo, 0m, Day(5, 2)));

        var supplier = Assert.Single(cost.Suppliers);
        Assert.Equal((120m, 100m), (supplier.Latest.UnitCost, supplier.Previous!.UnitCost));
        Assert.Equal(2, cost.PurchasesWithoutCost);
    }

    [Fact]
    public void AProductWithOnlyUncostedPurchases_HasNoSuppliersCompared_AndIsNotAnError()
    {
        var cost = Rices(Line(Acme, 0m, Day(5, 1)));

        Assert.Empty(cost.Suppliers);
        Assert.Null(cost.Gap);
        Assert.Equal(1, cost.PurchasesWithoutCost);
    }

    [Fact]
    public void TwoPurchasesPlacedAtTheSameInstant_AreOrderedByOrderId_WhateverTheInputOrder()
    {
        var low = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var high = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var a = Line(Acme, 100m, Day(5, 1), order: low);
        var b = Line(Acme, 150m, Day(5, 1), order: high);

        var forward = Assert.Single(Rices(a, b).Suppliers);
        var reversed = Assert.Single(Rices(b, a).Suppliers);

        Assert.Equal((150m, 100m), (forward.Latest.UnitCost, forward.Previous!.UnitCost));
        Assert.Equal(forward, reversed);
    }

    // ---- supplier gap

    [Fact]
    public void TheGap_IsBetweenTheCheapestAndTheMostExpensiveSuppliersLatestCosts()
    {
        var cost = Rices(Line(Acme, 100m, Day(5, 1)), Line(Bravo, 130m, Day(5, 2)), Line(Cargo, 115m, Day(5, 3)), Line(Bravo, 90m, Day(3, 1)));

        var gap = cost.Gap!;
        Assert.Equal(("Acme", "Bravo", 30m, true), (gap.Cheapest.SupplierName, gap.MostExpensive.SupplierName, gap.GapPercent, gap.Notable));
    }

    [Fact]
    public void AGapUnderTenPercent_IsNotNotable_AndASingleSupplierHasNoGap()
    {
        Assert.False(Rices(Line(Acme, 100m, Day(5, 1)), Line(Bravo, 109m, Day(5, 2))).Gap!.Notable);
        Assert.Null(Rices(Line(Acme, 100m, Day(5, 1)), Line(Acme, 150m, Day(5, 2))).Gap);
    }

    [Fact]
    public void ASupplierWithOnlyUncostedPurchases_IsNotInTheGap()
    {
        Assert.Null(Rices(Line(Acme, 100m, Day(5, 1)), Line(Bravo, 0m, Day(5, 2))).Gap);
    }

    // ---- volume and ordering

    [Fact]
    public void TheVolume_CoversOnlyTheRecentWindow_AndCountsOrdersQuantityAndSpend()
    {
        var order = Guid.CreateVersion7();
        var cost = Rices(
            Line(Acme, 10m, Day(4, 1), quantity: 100m),                    // before the window
            Line(Acme, 10m, Day(5, 2), quantity: 5m, order: order),        // first day of the window
            Line(Bravo, 20m, Day(5, 31), quantity: 2m),                    // last day of the window
            Line(Acme, 10m, new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero), quantity: 50m)); // today: not in the window

        Assert.Equal((2, 7m, 90m), (cost.Volume.Orders, cost.Volume.Quantity, cost.Volume.Spend));
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero), cost.LastPurchasedAtUtc);
    }

    [Fact]
    public void Products_AreOrderedBySpendInTheWindow_ThenLastPurchase_ThenName()
    {
        var costs = Calculate(
            Line(Acme, 10m, Day(5, 5), quantity: 1m, product: Rice),
            Line(Acme, 10m, Day(5, 5), quantity: 9m, product: Oil));

        Assert.Equal(["Oil", "Rice"], costs.Products.Select(p => p.Product.Name).ToArray());
    }

    [Fact]
    public void TheSameInput_GivesTheSameResult_WhateverTheInputOrder()
    {
        var lines = new[]
        {
            Line(Acme, 100m, Day(3, 1)), Line(Acme, 120m, Day(5, 1)), Line(Bravo, 90m, Day(5, 2)), Line(Cargo, 95m, Day(4, 2)),
            Line(Acme, 5m, Day(5, 3), product: Oil), Line(Bravo, 6m, Day(5, 4), product: Oil),
        };

        var forward = PurchaseCostCalculator.Calculate(lines, Suppliers, Products, Recent);
        var reversed = PurchaseCostCalculator.Calculate(lines.Reverse().ToArray(), Suppliers.Reverse().ToArray(), Products.Reverse().ToArray(), Recent);

        Assert.Equal(
            forward.Products.Select(p => (p.Product.Name, p.Volume, p.Gap?.GapPercent, string.Join(",", p.Suppliers.Select(s => s.SupplierName + s.ChangePercent)))),
            reversed.Products.Select(p => (p.Product.Name, p.Volume, p.Gap?.GapPercent, string.Join(",", p.Suppliers.Select(s => s.SupplierName + s.ChangePercent)))));
    }

    [Fact]
    public void WithNoPurchases_EverythingIsEmpty()
    {
        Assert.Empty(Calculate().Products);
    }

    // ---- the insights

    private static IReadOnlyList<Insight> Evaluate(params PurchasedLine[] lines) => PurchaseCostRule.Evaluate(Calculate(lines), Recent.StartUtc);

    [Fact]
    public void ANotableIncrease_IsAMediumInsight_NamingBothCostsDatesAndSupplier()
    {
        var insight = Assert.Single(Evaluate(Line(Acme, 100m, Day(4, 1)), Line(Acme, 125m, Day(5, 10))));

        Assert.Equal((PurchaseCostRule.CostChangeType, InsightSeverity.Medium, "Rice"), (insight.Type, insight.Severity, insight.Subject.Name));
        Assert.Equal("review_supplier_costs", insight.RecommendedAction.Code);
        Assert.Contains(insight.Facts, f => f.Key == "supplier_change_1" && f.Value == "Acme | 100 on 2026-04-01 to 125 on 2026-05-10 | +25%");
        Assert.Contains(insight.Facts, f => f.Key == "notable_change_threshold" && f.Value == "10");
        Assert.Equal("The cost of Rice from Acme rose from 100 to 125 (+25%) between the purchases of 2026-04-01 and 2026-05-10.", insight.Reason);
    }

    [Fact]
    public void ANotableDecrease_IsAnInfoInsight()
    {
        var insight = Assert.Single(Evaluate(Line(Acme, 100m, Day(4, 1)), Line(Acme, 80m, Day(5, 10))));

        Assert.Equal(InsightSeverity.Info, insight.Severity);
        Assert.Contains("fell from 100 to 80 (-20%)", insight.Reason);
    }

    [Fact]
    public void ADecreaseAndAnIncrease_OnTheSameProduct_AreOneMediumInsightNamingBoth()
    {
        var insights = Evaluate(
            Line(Acme, 100m, Day(4, 1)), Line(Acme, 150m, Day(5, 10)),
            Line(Bravo, 100m, Day(4, 1)), Line(Bravo, 80m, Day(5, 11)));

        Assert.Equal([PurchaseCostRule.CostChangeType, PurchaseCostRule.SupplierGapType], insights.Select(i => i.Type).ToArray()); // the latest costs 150 and 80 also differ notably
        var insight = insights[0];

        Assert.Equal(InsightSeverity.Medium, insight.Severity);
        Assert.Equal(2, insight.Facts.Count(f => f.Key.StartsWith("supplier_change_", StringComparison.Ordinal)));
        Assert.Contains("1 more supplier changed notably too", insight.Reason);
    }

    [Fact]
    public void AChangeBelowTheThreshold_ASinglePurchase_OrAnOldLatestPurchase_ProducesNoCostChangeInsight()
    {
        Assert.DoesNotContain(Evaluate(Line(Acme, 100m, Day(4, 1)), Line(Acme, 105m, Day(5, 10))), i => i.Type == PurchaseCostRule.CostChangeType);
        Assert.Empty(Evaluate(Line(Acme, 100m, Day(5, 10))));
        Assert.Empty(Evaluate(Line(Acme, 100m, Day(2, 1)), Line(Acme, 200m, Day(3, 1)))); // the change is not recent
    }

    [Fact]
    public void ANotableSupplierGap_IsALowInsight_NamingBothSuppliersAndCosts()
    {
        var insight = Assert.Single(Evaluate(Line(Acme, 100m, Day(5, 1)), Line(Bravo, 130m, Day(5, 2))));

        Assert.Equal((PurchaseCostRule.SupplierGapType, InsightSeverity.Low), (insight.Type, insight.Severity));
        Assert.Contains(insight.Facts, f => f.Key == "cheapest_supplier" && f.Value == "Acme");
        Assert.Contains(insight.Facts, f => f.Key == "most_expensive_unit_cost" && f.Value == "130");
        Assert.Contains(insight.Facts, f => f.Key == "gap_percent" && f.Value == "30");
        Assert.Equal("Bravo charged 130 for Rice on its latest purchase, 30% more than Acme at 100.", insight.Reason);
    }

    [Fact]
    public void AGapOnlyAmongOldPurchases_ProducesNoInsight()
    {
        Assert.Empty(Evaluate(Line(Acme, 100m, Day(3, 1)), Line(Bravo, 200m, Day(3, 2))));
    }

    [Fact]
    public void Insights_AreOrderedBySeverityThenName_AndEveryOneHasFactsAndAReason()
    {
        var insights = Evaluate(
            Line(Acme, 100m, Day(4, 1)), Line(Acme, 80m, Day(5, 10)),                 // Rice: decrease -> Info
            Line(Acme, 10m, Day(4, 1), product: Oil), Line(Acme, 20m, Day(5, 10), product: Oil)); // Oil: increase -> Medium

        Assert.Equal([("Oil", InsightSeverity.Medium), ("Rice", InsightSeverity.Info)], insights.Select(i => (i.Subject.Name, i.Severity)).ToArray());
        Assert.All(insights, i =>
        {
            Assert.NotEmpty(i.Facts);
            Assert.False(string.IsNullOrWhiteSpace(i.Reason));
        });
    }
}
