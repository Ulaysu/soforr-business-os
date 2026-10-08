using Soforr.Application.Intelligence;
using Soforr.Application.Intelligence.Rules;

namespace Soforr.Application.Tests.Intelligence;

public class OverdueBalanceRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Awa = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Bakary = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly Guid Cherno = Guid.Parse("00000000-0000-0000-0000-0000000000c3");
    private static readonly ReceivableCustomer[] People = [new(Awa, "Awa Jallow"), new(Bakary, "Bakary Ceesay"), new(Cherno, "Cherno Sowe")];

    private static ReceivableSale Sale(Guid customer, decimal owed, int daysPastDue, Guid? id = null)
    {
        var due = Now.AddDays(-daysPastDue).AddHours(-1);
        return new ReceivableSale(id ?? Guid.CreateVersion7(), customer, due.AddDays(-30), due, owed, 0m);
    }

    private static IReadOnlyList<Insight> Evaluate(decimal? threshold, params ReceivableSale[] sales) =>
        OverdueBalanceRule.Evaluate(ReceivablesCalculator.Calculate(sales, People, Now, TimeZoneInfo.Utc), threshold);

    private static string Fact(Insight insight, string key) => insight.Facts.Single(f => f.Key == key).Value;

    // ---- the materiality threshold

    [Fact]
    public void WithNoThresholdSet_EveryOverdueBalanceIsReported_HoweverSmall()
    {
        var insights = Evaluate(null, Sale(Awa, 0.0001m, 2), Sale(Bakary, 5000m, 2));

        Assert.Equal(["Bakary Ceesay", "Awa Jallow"], insights.Select(i => i.Subject.Name).ToArray());
        Assert.Equal("none set", Fact(insights[0], "materiality_threshold"));
    }

    [Theory]
    [InlineData(100, true)]    // exactly the threshold is material
    [InlineData(99.9999, true)]
    [InlineData(100.0001, false)]
    [InlineData(0, true)]
    public void ABalance_IsReported_WhenItReachesTheThreshold(double threshold, bool reported)
    {
        var insights = Evaluate((decimal)threshold, Sale(Awa, 100m, 2));

        Assert.Equal(reported, insights.Count == 1);
    }

    [Fact]
    public void TheThreshold_IsAppliedToTheCustomersWholeOverdueAmount_NotToEachSale()
    {
        // Three sales of 40 each are below a threshold of 100 one by one, but the customer is 120 overdue.
        var insights = Evaluate(100m, Sale(Awa, 40m, 2), Sale(Awa, 40m, 20), Sale(Awa, 40m, 40));

        var insight = Assert.Single(insights);
        Assert.Equal("120", Fact(insight, "overdue_amount"));
        Assert.Equal("100", Fact(insight, "materiality_threshold"));
    }

    [Fact]
    public void ABalanceThatIsNotYetDue_NeverProducesAnInsight_WhateverItsSize()
    {
        var notDue = new ReceivableSale(Guid.CreateVersion7(), Awa, Now.AddDays(-5), Now.AddDays(25), 1_000_000m, 0m);

        Assert.Empty(Evaluate(null, notDue));
    }

    [Fact]
    public void OnlyTheOverduePart_CountsTowardsTheThreshold_NotWhatIsNotYetDue()
    {
        var notDue = new ReceivableSale(Guid.CreateVersion7(), Awa, Now.AddDays(-5), Now.AddDays(25), 1000m, 0m);

        Assert.Empty(Evaluate(100m, notDue, Sale(Awa, 50m, 3)));
        Assert.Single(Evaluate(50m, notDue, Sale(Awa, 50m, 3)));
    }

    // ---- severity

    [Theory]
    [InlineData(0, InsightSeverity.Low)]
    [InlineData(30, InsightSeverity.Low)]
    [InlineData(31, InsightSeverity.Medium)]
    [InlineData(90, InsightSeverity.Medium)]
    [InlineData(91, InsightSeverity.High)]
    [InlineData(500, InsightSeverity.High)]
    public void TheSeverity_FollowsTheOldestOverdueAge(int daysPastDue, InsightSeverity expected)
    {
        var insight = Assert.Single(Evaluate(null, Sale(Awa, 10m, daysPastDue)));

        Assert.Equal(expected, insight.Severity);
    }

    [Fact]
    public void ACustomerWithAnyAmountNinetyOneDaysOrMorePastDue_IsHigh_EvenIfMostIsRecent()
    {
        var insight = Assert.Single(Evaluate(null, Sale(Awa, 1000m, 3), Sale(Awa, 1m, 120)));

        Assert.Equal(InsightSeverity.High, insight.Severity);
    }

    // ---- what an insight shows

    [Fact]
    public void AnInsight_NamesTheAmountTheDaysAndTheSalesBehindIt()
    {
        var oldest = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var newer = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var notYetDue = new ReceivableSale(Guid.CreateVersion7(), Awa, Now, Now.AddDays(10), 25m, 5m);

        var insight = Assert.Single(Evaluate(100m, Sale(Awa, 60m, 62, oldest), Sale(Awa, 90m, 4, newer), notYetDue));

        Assert.Equal("customers.overdue_balance", insight.Type);
        Assert.Equal(new InsightSubject("Customer", Awa, "Awa Jallow"), insight.Subject);
        Assert.Equal(InsightSeverity.Medium, insight.Severity);
        Assert.Equal(new InsightAction("collect_payment", "Collect from Awa Jallow"), insight.RecommendedAction);
        Assert.Equal(("150", "170", "20", "2", "62"), (Fact(insight, "overdue_amount"), Fact(insight, "outstanding_amount"), Fact(insight, "not_yet_due_amount"), Fact(insight, "overdue_sales"), Fact(insight, "oldest_days_past_due")));
        Assert.Equal(("90", "0", "60", "0"), (Fact(insight, "overdue_0_30_days"), Fact(insight, "overdue_31_60_days"), Fact(insight, "overdue_61_90_days"), Fact(insight, "overdue_91_plus_days")));
        Assert.Equal("2026-06-01 (UTC)", Fact(insight, "as_of"));
        Assert.StartsWith($"{oldest:D} | owes 60 | due 2026-03-31 | 62 days past due", Fact(insight, "overdue_sale_1"));
        Assert.StartsWith($"{newer:D} | owes 90 | due 2026-05-28 | 4 days past due", Fact(insight, "overdue_sale_2"));
        Assert.Equal("Awa Jallow has 150 overdue on 2 sales, the oldest 62 days past due; 170 is outstanding in all.", insight.Reason);
    }

    [Fact]
    public void ASingleSale_IsDescribedInTheSingular()
    {
        var insight = Assert.Single(Evaluate(null, Sale(Awa, 10m, 5)));

        Assert.Contains("on 1 sale,", insight.Reason);
    }

    [Fact]
    public void OnlyTheOldestFiveSales_AreNamed_AndTheRestAreCounted()
    {
        var sales = Enumerable.Range(1, 8).Select(i => Sale(Awa, 10m, i * 3)).ToArray();

        var insight = Assert.Single(Evaluate(null, sales));

        Assert.Equal(5, insight.Facts.Count(f => f.Key.StartsWith("overdue_sale_", StringComparison.Ordinal) && char.IsDigit(f.Key[^1])));
        Assert.Equal("3", Fact(insight, "overdue_sales_not_listed"));
        Assert.Contains("24 days past due", Fact(insight, "overdue_sale_1"));
        Assert.Contains("12 days past due", Fact(insight, "overdue_sale_5"));
    }

    [Fact]
    public void WhenEverySaleIsNamed_ThereIsNoNotListedFact()
    {
        var insight = Assert.Single(Evaluate(null, Sale(Awa, 10m, 5), Sale(Awa, 10m, 6)));

        Assert.DoesNotContain(insight.Facts, f => f.Key == "overdue_sales_not_listed");
    }

    // ---- ordering

    [Fact]
    public void TheInsights_AreOrderedBySeverity_ThenTheAmountOverdue_ThenName()
    {
        var insights = Evaluate(
            null,
            Sale(Awa, 900m, 10),      // Low, big
            Sale(Bakary, 20m, 120),   // High, small
            Sale(Cherno, 30m, 45));   // Medium

        Assert.Equal(["Bakary Ceesay", "Cherno Sowe", "Awa Jallow"], insights.Select(i => i.Subject.Name).ToArray());
        Assert.Equal([InsightSeverity.High, InsightSeverity.Medium, InsightSeverity.Low], insights.Select(i => i.Severity).ToArray());
    }

    [Fact]
    public void EveryInsight_HasFactsAndAReason_AndTheSameInputGivesTheSameInsights()
    {
        var sales = new[] { Sale(Awa, 10m, 5), Sale(Bakary, 20m, 50), Sale(Cherno, 30m, 150) };

        var first = Evaluate(null, sales);
        var second = Evaluate(null, sales.Reverse().ToArray());

        Assert.All(first, insight =>
        {
            Assert.NotEmpty(insight.Facts);
            Assert.False(string.IsNullOrWhiteSpace(insight.Reason));
        });
        Assert.Equal(first.Select(i => (i.Key, i.Severity, i.Reason)), second.Select(i => (i.Key, i.Severity, i.Reason)));
    }

    [Fact]
    public void WithNothingOverdue_ThereAreNoInsights()
    {
        Assert.Empty(Evaluate(null));
    }
}
