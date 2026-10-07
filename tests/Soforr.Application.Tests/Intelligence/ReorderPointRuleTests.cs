using Soforr.Application.Intelligence;
using Soforr.Application.Intelligence.Rules;

namespace Soforr.Application.Tests.Intelligence;

public class ReorderPointRuleTests
{
    private static ProductStockSnapshot Product(string name, decimal quantity, decimal? reorderPoint = 10m, int? leadTime = 7, bool active = true, Guid? id = null, string sku = "SKU", string unit = "bag") =>
        new(id ?? Guid.CreateVersion7(), sku, name, unit, active, quantity, reorderPoint, leadTime);

    [Fact]
    public void AProductAtOrBelowItsReorderPoint_IsReported_AboveItIsNot()
    {
        var below = Product("Below", 4m);
        var exactly = Product("Exactly", 10m);
        var above = Product("Above", 10.0001m);

        var insights = ReorderPointRule.Evaluate([below, exactly, above]);

        Assert.Equal(["Below", "Exactly"], insights.Select(i => i.Subject.Name).Order().ToArray());
        Assert.DoesNotContain(insights, i => i.Subject.Name == "Above");
    }

    [Fact]
    public void AnInsight_NamesTheProductTheNumbersTheReasonAndTheAction()
    {
        var id = Guid.CreateVersion7();
        var insight = Assert.Single(ReorderPointRule.Evaluate([Product("Rice 25kg", 4m, reorderPoint: 10m, leadTime: 7, id: id, sku: "RICE-25")]));

        Assert.Equal("inventory.reorder_point_reached", insight.Type);
        Assert.Equal(InsightSeverity.Medium, insight.Severity);
        Assert.Equal(new InsightSubject("Product", id, "Rice 25kg"), insight.Subject);
        Assert.Equal(
            [("sku", "RICE-25", (string?)null), ("stock", "4", "bag"), ("reorder_point", "10", "bag"), ("lead_time_days", "7", "days")],
            insight.Facts.Select(f => (f.Key, f.Value, f.Unit)).ToArray());
        Assert.Equal("Rice 25kg is at or below its reorder point: 4 bag in stock, reorder point 10.", insight.Reason);
        Assert.Equal(new InsightAction("create_purchase_order", "Order more Rice 25kg"), insight.RecommendedAction);
    }

    [Fact]
    public void AProductWithNoStockLeft_IsHighSeverity_AndSaysItIsOutOfStock()
    {
        var insight = Assert.Single(ReorderPointRule.Evaluate([Product("Rice", 0m, reorderPoint: 5m)]));

        Assert.Equal(InsightSeverity.High, insight.Severity);
        Assert.Equal("Rice is out of stock (reorder point 5 bag).", insight.Reason);
        Assert.Equal("0", insight.Facts.Single(f => f.Key == "stock").Value);
    }

    [Fact]
    public void AReorderPointOfZero_IsLowOnlyWhenThereIsNoStock()
    {
        Assert.Single(ReorderPointRule.Evaluate([Product("Empty", 0m, reorderPoint: 0m)]));
        Assert.Empty(ReorderPointRule.Evaluate([Product("Some", 0.0001m, reorderPoint: 0m)]));
    }

    [Fact]
    public void InactiveProducts_AndProductsWithoutReorderSettings_NeverProduceAnInsight()
    {
        var insights = ReorderPointRule.Evaluate([Product("Inactive", 0m, active: false), Product("NoSettings", 0m, reorderPoint: null, leadTime: null)]);

        Assert.Empty(insights);
    }

    [Fact]
    public void ALeadTimeIsOmittedFromTheFacts_WhenThereIsNone()
    {
        var insight = Assert.Single(ReorderPointRule.Evaluate([Product("Rice", 2m, leadTime: null)]));

        Assert.DoesNotContain(insight.Facts, f => f.Key == "lead_time_days");
    }

    [Fact]
    public void ThereAreNoInsights_ForNoProducts()
    {
        Assert.Empty(ReorderPointRule.Evaluate([]));
    }

    [Fact]
    public void TheResultIsOrdered_OutOfStockFirst_ThenTheEmptiestStock_ThenNameThenId()
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
        };

        var names = ReorderPointRule.Evaluate(products).Select(i => (i.Subject.Name, i.Subject.Id)).ToArray();

        Assert.Equal("Gone", names[0].Name);
        Assert.Equal("Mango", names[1].Name);
        Assert.Equal(["Apple", "Same", "Same", "Zebra"], names.Skip(2).Select(n => n.Name).ToArray());
        Assert.Equal([firstId, secondId], names.Where(n => n.Name == "Same").Select(n => n.Id!.Value).ToArray());
    }

    [Fact]
    public void TheSameInput_GivesTheSameResult_WhateverTheInputOrder()
    {
        var products = Enumerable.Range(0, 20).Select(i => Product("P" + (i % 7), i % 5, id: Guid.Parse($"00000000-0000-0000-0000-{i:D12}"))).ToArray();

        var forward = ReorderPointRule.Evaluate(products).Select(i => i.Key).ToArray();
        var again = ReorderPointRule.Evaluate(products).Select(i => i.Key).ToArray();
        var reversed = ReorderPointRule.Evaluate(products.Reverse().ToArray()).Select(i => i.Key).ToArray();

        Assert.Equal(forward, again);
        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void EveryInsight_HasFactsAndAReason()
    {
        var insights = ReorderPointRule.Evaluate([Product("A", 0m), Product("B", 5m), Product("C", 10m, leadTime: null)]);

        Assert.All(insights, insight =>
        {
            Assert.NotEmpty(insight.Facts);
            Assert.False(string.IsNullOrWhiteSpace(insight.Reason));
        });
    }
}
