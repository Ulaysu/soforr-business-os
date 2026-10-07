using Soforr.Application.Intelligence;

namespace Soforr.Application.Tests.Intelligence;

public class InsightTests
{
    private static readonly Guid ProductId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c3a11");

    private static Insight Valid(
        string type = "inventory.reorder_point_reached",
        InsightSeverity severity = InsightSeverity.Medium,
        InsightSubject? subject = null,
        IReadOnlyList<InsightFact>? facts = null,
        string reason = "Rice is low.",
        InsightAction? action = null) =>
        Insight.Create(
            type,
            severity,
            subject ?? new InsightSubject("Product", ProductId, "Rice"),
            facts ?? [InsightFact.Number("stock", 4m, "bag")],
            reason,
            action ?? new InsightAction("create_purchase_order", "Order more Rice"));

    [Fact]
    public void AnInsight_CarriesItsFactsReasonAndAction()
    {
        var insight = Valid();

        Assert.Equal("inventory.reorder_point_reached", insight.Type);
        Assert.Equal(InsightSeverity.Medium, insight.Severity);
        Assert.Equal("Rice", insight.Subject.Name);
        var fact = Assert.Single(insight.Facts);
        Assert.Equal(("stock", "4", "bag"), (fact.Key, fact.Value, fact.Unit));
        Assert.Equal("Rice is low.", insight.Reason);
        Assert.Equal("create_purchase_order", insight.RecommendedAction.Code);
    }

    [Fact]
    public void TheKey_IsStable_ForTheSameTypeAndSubject_AndDiffersOtherwise()
    {
        var first = Valid();
        var again = Valid(severity: InsightSeverity.High, reason: "Different words.");
        var otherSubject = Valid(subject: new InsightSubject("Product", Guid.CreateVersion7(), "Oil"));
        var otherType = Valid(type: "inventory.something_else");

        Assert.Equal(first.Key, again.Key);
        Assert.NotEqual(first.Key, otherSubject.Key);
        Assert.NotEqual(first.Key, otherType.Key);
        Assert.Equal("inventory.reorder_point_reached:Product:0197f6a23b1c7c1e9d2a5e8f4b6c3a11", first.Key);
    }

    [Fact]
    public void ASubjectWithoutAnId_StillHasAKey()
    {
        var insight = Valid(subject: new InsightSubject("Organization", null, "Whole business"));

        Assert.Equal("inventory.reorder_point_reached:Organization:-", insight.Key);
    }

    [Fact]
    public void AnInsightWithoutFacts_CannotBeCreated()
    {
        Assert.Throws<ArgumentException>(() => Valid(facts: []));
    }

    [Theory]
    [InlineData("", "v")]
    [InlineData("k", "")]
    [InlineData("  ", "v")]
    public void AFactNeedsAKeyAndAValue(string key, string value)
    {
        Assert.Throws<ArgumentException>(() => Valid(facts: [new InsightFact(key, value)]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnInsightWithoutAReasonOrType_CannotBeCreated(string text)
    {
        Assert.Throws<ArgumentException>(() => Valid(reason: text));
        Assert.Throws<ArgumentException>(() => Valid(type: text));
    }

    [Fact]
    public void AnInsightWithoutASubjectNameOrAction_CannotBeCreated()
    {
        Assert.Throws<ArgumentException>(() => Valid(subject: new InsightSubject("Product", ProductId, " ")));
        Assert.Throws<ArgumentException>(() => Valid(subject: new InsightSubject(" ", ProductId, "Rice")));
        Assert.Throws<ArgumentException>(() => Valid(action: new InsightAction("", "Label")));
        Assert.Throws<ArgumentException>(() => Valid(action: new InsightAction("code", " ")));
        Assert.Throws<ArgumentNullException>(() => Insight.Create("t", InsightSeverity.Low, new InsightSubject("P", null, "N"), [InsightFact.Text("k", "v")], "r", null!));
    }

    [Fact]
    public void AnUnknownSeverity_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Valid(severity: (InsightSeverity)99));
    }

    [Fact]
    public void TheFacts_AreCopied_SoLaterChangesToTheSourceListDoNotChangeTheInsight()
    {
        var facts = new List<InsightFact> { InsightFact.Number("stock", 4m) };
        var insight = Valid(facts: facts);

        facts.Add(InsightFact.Number("extra", 1m));

        Assert.Single(insight.Facts);
    }

    [Theory]
    [InlineData(4, "4")]
    [InlineData(4.5, "4.5")]
    [InlineData(0.3333, "0.3333")]
    [InlineData(1234567.1, "1234567.1")]
    [InlineData(0, "0")]
    [InlineData(-2.5, "-2.5")]
    public void NumbersAreFormattedInTheInvariantCulture_WithoutGroupingOrTrailingZeros(double number, string expected)
    {
        Assert.Equal(expected, InsightFact.Number("k", (decimal)number).Value);
    }

    [Fact]
    public void SeverityHasAFixedOrder()
    {
        Assert.True(InsightSeverity.High > InsightSeverity.Medium);
        Assert.True(InsightSeverity.Medium > InsightSeverity.Low);
        Assert.True(InsightSeverity.Low > InsightSeverity.Info);
        Assert.Equal([1, 2, 3, 4], Enum.GetValues<InsightSeverity>().Select(value => (int)value).Order().ToArray());
    }
}
