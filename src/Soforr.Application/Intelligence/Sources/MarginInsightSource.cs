using Soforr.Application.Intelligence.Rules;
using Soforr.Application.Persistence;

namespace Soforr.Application.Intelligence.Sources;

/// <summary>
/// The margin insights for the attention list, from <see cref="MarginCalculator"/> over the last <see cref="PeriodDays"/> complete
/// local days compared with the <see cref="PeriodDays"/> days before them. They reveal cost and margin, so the source requires
/// <c>ProductViewCost</c> and the evaluation runs it only for a caller who holds it.
/// <list type="bullet">
/// <item><description><c>margin.negative_margin</c> (<b>High</b>): a product whose costed sales of the current period brought in less than they cost
/// (gross margin below zero). It costs money now.</description></item>
/// <item><description><c>margin.margin_decline</c> (<b>Medium</b>): a product whose margin percentage fell by at least
/// <see cref="MarginCalculator.DeclineThresholdPoints"/> percentage points against the previous period, with a margin in both. Margin eroding
/// needs action soon.</description></item>
/// </list>
/// A product gets at most one: a negative margin takes precedence. Lines with no recorded cost are left out, and a product without a
/// margin in a period is never guessed at, so it produces no insight. Every insight shows the previous and the current price, cost
/// and margin.
/// </summary>
public sealed class MarginInsightSource(IAppDbContext db) : IInsightSource
{
    public const int PeriodDays = 30;

    public const string NegativeMarginType = "margin.negative_margin";

    public const string MarginDeclineType = "margin.margin_decline";

    public const string ReviewAction = "review_product_pricing";

    public bool RequiresProductViewCost => true;

    public async Task<IReadOnlyList<Insight>> EvaluateAsync(InsightContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var input = await SalesMetricsGatherer.GatherAsync(db, context.NowUtc, PeriodDays, cancellationToken);
        var metrics = MarginCalculator.Calculate(input.Lines, input.Products, input.Current, input.Previous);

        var insights = new List<Insight>();
        foreach (var margin in metrics.Products)
        {
            if (margin.Current.GrossMargin < 0m)
            {
                insights.Add(NegativeMargin(margin, input));
            }
            else if (MarginCalculator.HasDeclined(margin))
            {
                insights.Add(Decline(margin, input));
            }
        }

        return insights;
    }

    private static Insight NegativeMargin(ProductMargin margin, SalesMetricsInput input)
    {
        var product = margin.Product;
        var now = margin.Current;

        var facts = Facts(margin, input);
        var reason = $"{product.Name} sold at a loss in the last {input.Current.Days} days: {Format(now.Revenue)} of sales cost {Format(now.Cost)}, a margin of {Format(now.GrossMargin)}"
            + (now.MarginPercent is { } percent ? $" ({Format(percent)}%)." : ".")
            + Comparison(margin);

        return Insight.Create(
            NegativeMarginType,
            InsightSeverity.High,
            new InsightSubject("Product", product.ProductId, product.Name),
            facts,
            reason,
            new InsightAction(ReviewAction, $"Review the price of {product.Name}"));
    }

    private static Insight Decline(ProductMargin margin, SalesMetricsInput input)
    {
        var product = margin.Product;
        var now = margin.Current;
        var before = margin.Previous;

        var facts = Facts(margin, input);
        facts.Add(InsightFact.Number("margin_change_points", margin.MarginChangePoints!.Value, "points"));

        return Insight.Create(
            MarginDeclineType,
            InsightSeverity.Medium,
            new InsightSubject("Product", product.ProductId, product.Name),
            facts,
            $"The margin on {product.Name} fell from {Format(before.MarginPercent!.Value)}% to {Format(now.MarginPercent!.Value)}% ({Format(Math.Abs(margin.MarginChangePoints.Value))} points) between the {input.Previous.Days} days before and the last {input.Current.Days} days." + Comparison(margin),
            new InsightAction(ReviewAction, $"Review the price of {product.Name}"));
    }

    /// <summary>The previous and the current price, cost and margin side by side, so the reader can see what moved.</summary>
    private static string Comparison(ProductMargin margin)
    {
        var now = margin.Current;
        var before = margin.Previous;
        if (now.AverageUnitPrice is not { } price || now.AverageUnitCost is not { } cost || before.AverageUnitPrice is not { } previousPrice || before.AverageUnitCost is not { } previousCost)
        {
            return string.Empty;
        }

        return $" Average price {Format(previousPrice)} to {Format(price)}, average cost {Format(previousCost)} to {Format(cost)}.";
    }

    private static List<InsightFact> Facts(ProductMargin margin, SalesMetricsInput input)
    {
        var product = margin.Product;
        var now = margin.Current;
        var before = margin.Previous;
        var facts = new List<InsightFact>
        {
            InsightFact.Text("sku", product.Sku),
            InsightFact.Number("revenue", now.Revenue),
            InsightFact.Number("cost", now.Cost),
            InsightFact.Number("gross_margin", now.GrossMargin),
        };
        AddOptional(facts, "margin_percent", now.MarginPercent, "%");
        AddOptional(facts, "average_unit_price", now.AverageUnitPrice);
        AddOptional(facts, "average_unit_cost", now.AverageUnitCost);
        facts.Add(InsightFact.Number("previous_revenue", before.Revenue));
        facts.Add(InsightFact.Number("previous_cost", before.Cost));
        AddOptional(facts, "previous_margin_percent", before.MarginPercent, "%");
        AddOptional(facts, "previous_average_unit_price", before.AverageUnitPrice);
        AddOptional(facts, "previous_average_unit_cost", before.AverageUnitCost);
        if (now.RevenueWithoutCost > 0m)
        {
            facts.Add(InsightFact.Number("revenue_without_recorded_cost", now.RevenueWithoutCost));
        }

        facts.Add(InsightFact.Number("period_days", input.Current.Days, "days"));
        facts.Add(InsightFact.Text("period", Describe(input.Current)));
        facts.Add(InsightFact.Text("previous_period", Describe(input.Previous)));
        return facts;
    }

    private static void AddOptional(List<InsightFact> facts, string key, decimal? value, string? unit = null)
    {
        if (value is { } present)
        {
            facts.Add(InsightFact.Number(key, present, unit));
        }
    }

    private static string Describe(InsightWindow window) => $"{window.FirstDay:yyyy-MM-dd} to {window.LastDay:yyyy-MM-dd} ({window.TimeZoneId})";

    private static string Format(decimal value) => InsightFact.Number("x", value).Value;
}
