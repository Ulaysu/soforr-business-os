using Soforr.Application.Intelligence;
using Soforr.Application.Intelligence.Rules;

namespace Soforr.Application.Tests.Intelligence;

public class InventoryRiskRuleTests
{
    // 30 complete days before 2026-06-01 in UTC: 2026-05-02 to 2026-05-31.
    private static readonly InsightWindow Window = InsightWindows.TrailingCompleteDays(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc, 30);

    private static ProductStockSnapshot Product(
        string name,
        decimal quantity,
        decimal? reorderPoint = 10m,
        int? leadTime = 7,
        decimal sold = 0m,
        bool active = true,
        Guid? id = null,
        string sku = "SKU",
        string unit = "bag") =>
        new(id ?? Guid.CreateVersion7(), sku, name, unit, active, quantity, reorderPoint, leadTime, sold);

    private static string FactValue(Insight insight, string key) => insight.Facts.Single(f => f.Key == key).Value;

    private static Insight Single(params ProductStockSnapshot[] products) => Assert.Single(InventoryRiskRule.Evaluate(products, Window));

    // ---- the reorder point (INTEL-001 behaviour, unchanged)

    [Fact]
    public void AProductAtOrBelowItsReorderPoint_IsReported_AboveItIsNot()
    {
        var insights = InventoryRiskRule.Evaluate([Product("Below", 4m), Product("Exactly", 10m), Product("Above", 10.0001m)], Window);

        Assert.Equal(["Below", "Exactly"], insights.Select(i => i.Subject.Name).Order().ToArray());
    }

    [Fact]
    public void AProductWithNoStockLeft_IsHighSeverity_AndSaysItIsOutOfStock()
    {
        var insight = Single(Product("Rice", 0m, reorderPoint: 5m));

        Assert.Equal(InsightSeverity.High, insight.Severity);
        Assert.Equal("inventory.reorder_point_reached", insight.Type);
        Assert.Equal("Rice is out of stock (reorder point 5 bag). It has had no sales in the last 30 days.", insight.Reason);
    }

    [Fact]
    public void AReorderPointOfZero_IsLowOnlyWhenThereIsNoStock()
    {
        Assert.Single(InventoryRiskRule.Evaluate([Product("Empty", 0m, reorderPoint: 0m)], Window));
        Assert.Empty(InventoryRiskRule.Evaluate([Product("Some", 0.0001m, reorderPoint: 0m)], Window));
    }

    [Fact]
    public void InactiveProducts_AndProductsWithoutReorderSettings_NeverProduceAnInsight_EvenWithFastSales()
    {
        var insights = InventoryRiskRule.Evaluate(
            [Product("Inactive", 0m, active: false, sold: 300m), Product("NoSettings", 0m, reorderPoint: null, leadTime: null, sold: 300m)],
            Window);

        Assert.Empty(insights);
    }

    // ---- no sales: no coverage, never infinite

    [Fact]
    public void WithNoSalesInTheWindow_ThereIsNoCoverage_JustTheReorderPoint_AndTheSuggestionIsTheGapToTheReorderPoint()
    {
        var insight = Single(Product("Rice 25kg", 4m, reorderPoint: 10m, leadTime: 7, sold: 0m, sku: "RICE-25"));

        Assert.Equal(InsightSeverity.Medium, insight.Severity);
        Assert.Equal("inventory.reorder_point_reached", insight.Type);
        Assert.Equal(
            [
                ("sku", "RICE-25", (string?)null),
                ("stock", "4", "bag"),
                ("reorder_point", "10", "bag"),
                ("lead_time_days", "7", "days"),
                ("sold_in_window", "0", "bag"),
                ("window_days", "30", "days"),
                ("window", "2026-05-02 to 2026-05-31 (UTC)", null),
                ("average_daily_sales", "0", "bag/day"),
                ("coverage", "no recent sales", null),
                ("suggested_order_quantity", "6", "bag"),
            ],
            insight.Facts.Select(f => (f.Key, f.Value, f.Unit)).ToArray());
        Assert.DoesNotContain(insight.Facts, f => f.Key == "coverage_days");
        Assert.Equal("Rice 25kg is at or below its reorder point: 4 bag in stock, reorder point 10. It has had no sales in the last 30 days.", insight.Reason);
        Assert.Equal(new InsightAction("create_purchase_order", "Order more Rice 25kg"), insight.RecommendedAction);
    }

    [Fact]
    public void NoSuggestion_IsGiven_WhenThereIsNothingToOrder()
    {
        var insight = Single(Product("Rice", 10m, reorderPoint: 10m, leadTime: 7, sold: 0m));

        Assert.DoesNotContain(insight.Facts, f => f.Key == "suggested_order_quantity");
    }

    // ---- coverage and the lead time

    [Fact]
    public void AtTheReorderPoint_WithCoverageShorterThanTheLeadTime_IsHigh_WithCoverageAndASuggestion()
    {
        // 60 sold in 30 days = 2 a day; 4 in stock lasts 2 days; restocking takes 7. Suggest 10 + 2 x 7 - 4 = 20.
        var insight = Single(Product("Rice", 4m, reorderPoint: 10m, leadTime: 7, sold: 60m));

        Assert.Equal(InsightSeverity.High, insight.Severity);
        Assert.Equal("inventory.reorder_point_reached", insight.Type);
        Assert.Equal("2", FactValue(insight, "average_daily_sales"));
        Assert.Equal("2", FactValue(insight, "coverage_days"));
        Assert.Equal("20", FactValue(insight, "suggested_order_quantity"));
        Assert.Equal("60", FactValue(insight, "sold_in_window"));
        Assert.Equal(
            "Rice is at or below its reorder point: 4 bag in stock, reorder point 10. At about 2 bag/day over the last 30 days it lasts about 2 days, and restocking takes 7 days.",
            insight.Reason);
    }

    [Fact]
    public void AtTheReorderPoint_WithCoverageThatOutlastsTheLeadTime_IsMedium()
    {
        // 30 sold = 1 a day; 8 in stock lasts 8 days; restocking takes 7. Suggest 10 + 1 x 7 - 8 = 9.
        var insight = Single(Product("Rice", 8m, reorderPoint: 10m, leadTime: 7, sold: 30m));

        Assert.Equal(InsightSeverity.Medium, insight.Severity);
        Assert.Equal("8", FactValue(insight, "coverage_days"));
        Assert.Equal("9", FactValue(insight, "suggested_order_quantity"));
    }

    [Fact]
    public void AboveTheReorderPoint_CoverageShorterThanTheLeadTime_IsARunOutInsight_AndHigh()
    {
        // 150 sold = 5 a day; 20 in stock lasts 4 days; restocking takes 7. Suggest 10 + 5 x 7 - 20 = 25.
        var insight = Single(Product("Rice", 20m, reorderPoint: 10m, leadTime: 7, sold: 150m));

        Assert.Equal("inventory.runs_out_before_restock", insight.Type);
        Assert.Equal(InsightSeverity.High, insight.Severity);
        Assert.Equal("4", FactValue(insight, "coverage_days"));
        Assert.Equal("25", FactValue(insight, "suggested_order_quantity"));
        Assert.Equal(
            "Rice may run out before a new order could arrive: 20 bag in stock. At about 5 bag/day over the last 30 days it lasts about 4 days, and restocking takes 7 days.",
            insight.Reason);
    }

    [Fact]
    public void CoverageEqualToTheLeadTime_IsNotShorter_SoNothingIsReported_WhenAboveTheReorderPoint()
    {
        // 30 sold = 1 a day; 7 in stock lasts exactly 7 days; restocking takes 7.
        Assert.Empty(InventoryRiskRule.Evaluate([Product("Rice", 7m, reorderPoint: 5m, leadTime: 7, sold: 30m)], Window));
    }

    [Fact]
    public void AboveTheReorderPoint_WithPlentyOfCoverage_IsNotReported()
    {
        Assert.Empty(InventoryRiskRule.Evaluate([Product("Rice", 100m, reorderPoint: 10m, leadTime: 7, sold: 30m)], Window));
    }

    [Fact]
    public void AboveTheReorderPoint_WithNoSales_IsNotReported_BecauseThereIsNoVelocityToRunOutAt()
    {
        Assert.Empty(InventoryRiskRule.Evaluate([Product("Rice", 11m, reorderPoint: 10m, leadTime: 7, sold: 0m)], Window));
    }

    [Fact]
    public void ZeroStockWithSales_HasZeroCoverage_AndASuggestionOfTheReorderPointPlusTheLeadTimeDemand()
    {
        // 30 sold = 1 a day; nothing in stock. Suggest 10 + 1 x 7 - 0 = 17.
        var insight = Single(Product("Rice", 0m, reorderPoint: 10m, leadTime: 7, sold: 30m));

        Assert.Equal(InsightSeverity.High, insight.Severity);
        Assert.Equal("0", FactValue(insight, "coverage_days"));
        Assert.Equal("17", FactValue(insight, "suggested_order_quantity"));
        Assert.StartsWith("Rice is out of stock (reorder point 10 bag).", insight.Reason);
    }

    [Fact]
    public void WithNoLeadTime_OnlyTheReorderPointRaisesAnInsight_AndNoQuantityIsSuggested()
    {
        var atPoint = Single(Product("Rice", 4m, reorderPoint: 10m, leadTime: null, sold: 60m));
        var fast = InventoryRiskRule.Evaluate([Product("Fast", 20m, reorderPoint: 10m, leadTime: null, sold: 3000m)], Window);

        Assert.Equal(InsightSeverity.Medium, atPoint.Severity);
        Assert.Equal("2", FactValue(atPoint, "coverage_days"));
        Assert.DoesNotContain(atPoint.Facts, f => f.Key is "suggested_order_quantity" or "lead_time_days");
        Assert.EndsWith("it lasts about 2 days.", atPoint.Reason);
        Assert.Empty(fast);
    }

    [Fact]
    public void ARunOut_AndTheReorderPoint_NeverGiveTwoInsightsForOneProduct()
    {
        var id = Guid.CreateVersion7();

        var insights = InventoryRiskRule.Evaluate([Product("Rice", 4m, reorderPoint: 10m, leadTime: 7, sold: 600m, id: id)], Window);

        var insight = Assert.Single(insights);
        Assert.Equal("inventory.reorder_point_reached", insight.Type);
    }

    // ---- rounding

    [Fact]
    public void Rounding_ShowsCoverageToOneDecimal_AverageToTwo_AndTheSuggestionToFour_HalfAwayFromZero_ComparingUnrounded()
    {
        // 100 sold in 30 days = 3.3333.. a day. Stock 7 lasts 2.1 days (7 x 30 / 100 exactly).
        // Suggest 10 + 3.3333.. x 7 - 7 = 26.3333.. -> 26.3333.
        var insight = Single(Product("Rice", 7m, reorderPoint: 10m, leadTime: 7, sold: 100m));

        Assert.Equal("3.33", FactValue(insight, "average_daily_sales"));
        Assert.Equal("2.1", FactValue(insight, "coverage_days"));
        Assert.Equal("26.3333", FactValue(insight, "suggested_order_quantity"));
    }

    [Fact]
    public void CoverageIsRoundedHalfAwayFromZero_ButComparedUnrounded()
    {
        // Stock 6.95 and 30 sold in 30 days = 1 a day: coverage 6.95, shown as 7.0 (half away from zero), but 6.95 < 7 days of lead time.
        var insight = Single(Product("Rice", 6.95m, reorderPoint: 5m, leadTime: 7, sold: 30m));

        Assert.Equal("inventory.runs_out_before_restock", insight.Type);
        Assert.Equal("7", FactValue(insight, "coverage_days"));
    }

    // ---- ordering, determinism, traceability

    [Fact]
    public void TheResultIsOrdered_BySeverity_ThenTheEmptiestStock_ThenNameThenId()
    {
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var products = new[]
        {
            Product("Zebra", 3m),
            Product("Apple", 3m),
            Product("Mango", 1m),
            Product("Same", 3m, id: secondId),
            Product("Same", 3m, id: firstId),
            Product("Gone", 0m),
            Product("Fast", 20m, sold: 150m),
        };

        var names = InventoryRiskRule.Evaluate(products, Window).Select(i => (i.Subject.Name, i.Subject.Id)).ToArray();

        // High first (Gone is out of stock, Fast runs out), emptiest stock first within a severity.
        Assert.Equal(["Gone", "Fast", "Mango", "Apple", "Same", "Same", "Zebra"], names.Select(n => n.Name).ToArray());
        Assert.Equal([firstId, secondId], names.Where(n => n.Name == "Same").Select(n => n.Id!.Value).ToArray());
    }

    [Fact]
    public void TheSameInput_GivesTheSameResult_WhateverTheInputOrder()
    {
        var products = Enumerable.Range(0, 24)
            .Select(i => Product("P" + (i % 7), i % 5, reorderPoint: 3m, sold: (i % 4) * 10m, id: Guid.Parse($"00000000-0000-0000-0000-{i:D12}")))
            .ToArray();

        var forward = InventoryRiskRule.Evaluate(products, Window).Select(i => (i.Key, i.Severity, i.Reason)).ToArray();
        var again = InventoryRiskRule.Evaluate(products, Window).Select(i => (i.Key, i.Severity, i.Reason)).ToArray();
        var reversed = InventoryRiskRule.Evaluate(products.Reverse().ToArray(), Window).Select(i => (i.Key, i.Severity, i.Reason)).ToArray();

        Assert.Equal(forward, again);
        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void EveryInsight_HasFactsAReasonAndTheWindowItWasBuiltFrom()
    {
        var insights = InventoryRiskRule.Evaluate([Product("A", 0m), Product("B", 5m, sold: 90m), Product("C", 10m, leadTime: null), Product("D", 20m, sold: 150m)], Window);

        Assert.Equal(4, insights.Count);
        Assert.All(insights, insight =>
        {
            Assert.NotEmpty(insight.Facts);
            Assert.False(string.IsNullOrWhiteSpace(insight.Reason));
            Assert.Equal("30", FactValue(insight, "window_days"));
            Assert.Equal("2026-05-02 to 2026-05-31 (UTC)", FactValue(insight, "window"));
            Assert.Contains(insight.Facts, f => f.Key is "coverage_days" or "coverage");
        });
    }

    [Fact]
    public void ThereAreNoInsights_ForNoProducts()
    {
        Assert.Empty(InventoryRiskRule.Evaluate([], Window));
    }

    [Fact]
    public void ADifferentWindowLength_ChangesTheAverage()
    {
        var sevenDays = InsightWindows.TrailingCompleteDays(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc, 7);

        var insight = Assert.Single(InventoryRiskRule.Evaluate([Product("Rice", 4m, reorderPoint: 10m, leadTime: 7, sold: 14m)], sevenDays));

        Assert.Equal("2", FactValue(insight, "average_daily_sales"));
        Assert.Equal("7", FactValue(insight, "window_days"));
        Assert.Equal("2026-05-25 to 2026-05-31 (UTC)", FactValue(insight, "window"));
    }
}
