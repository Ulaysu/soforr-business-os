using Soforr.Application.Intelligence.Rules;
using Soforr.Application.Persistence;

namespace Soforr.Application.Intelligence.Sources;

/// <summary>
/// The sales insights for the attention list: products whose sales are <b>declining</b> and the <b>slowest sellers</b>, from
/// <see cref="SalesMetricsCalculator"/> over the last <see cref="PeriodDays"/> complete local days compared with the
/// <see cref="PeriodDays"/> days before them. Severity is <c>Low</c> for both: they are worth knowing and cost money slowly, not now.
/// <para>
/// The slow-mover insights are produced only when at least <see cref="SalesMetricsCalculator.SlowMoverMinimumProducts"/> products
/// qualify. The summary shows the whole ordered list below that number, but on the attention list that would flag every product of
/// a very small shop as "slow", so there they are left out.
/// </para>
/// </summary>
public sealed class SalesInsightSource(IAppDbContext db) : IInsightSource
{
    public const int PeriodDays = 30;

    public const string DecliningType = "sales.declining_product";

    public const string SlowMoverType = "sales.slow_mover";

    public const string ReviewAction = "review_product";

    public bool RequiresProductViewCost => false;

    public async Task<IReadOnlyList<Insight>> EvaluateAsync(InsightContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var input = await SalesMetricsGatherer.GatherAsync(db, context.NowUtc, PeriodDays, cancellationToken);
        var metrics = SalesMetricsCalculator.Calculate(input.Lines, input.Products, input.Current, input.Previous, topCount: 1);

        var insights = new List<Insight>();
        insights.AddRange(metrics.DecliningProducts.Select(item => Declining(item, input)));
        if (!metrics.SlowMovers.ShowsFullList)
        {
            insights.AddRange(metrics.SlowMovers.Items.Select(item => SlowMover(item, metrics.SlowMovers.QualifyingProducts, input)));
        }

        return insights;
    }

    private static Insight Declining(DecliningProduct item, SalesMetricsInput input)
    {
        var product = item.Sales.Product;
        var unit = product.UnitOfMeasure;

        return Insight.Create(
            DecliningType,
            InsightSeverity.Low,
            new InsightSubject("Product", product.ProductId, product.Name),
            [
                InsightFact.Text("sku", product.Sku),
                InsightFact.Number("quantity_sold", item.Sales.Quantity, unit),
                InsightFact.Number("previous_quantity_sold", item.Sales.PreviousQuantity, unit),
                InsightFact.Number("decline_percent", item.DeclinePercent, "%"),
                InsightFact.Number("period_days", input.Current.Days, "days"),
                InsightFact.Text("period", Describe(input.Current)),
                InsightFact.Text("previous_period", Describe(input.Previous)),
            ],
            $"{product.Name} sold {Format(item.Sales.Quantity)} {unit} in the last {input.Current.Days} days, down {Format(item.DeclinePercent)}% from {Format(item.Sales.PreviousQuantity)} {unit} in the {input.Previous.Days} days before.",
            new InsightAction(ReviewAction, $"Review {product.Name}"));
    }

    private static Insight SlowMover(ProductSales item, int qualifying, SalesMetricsInput input)
    {
        var product = item.Product;
        var unit = product.UnitOfMeasure;

        return Insight.Create(
            SlowMoverType,
            InsightSeverity.Low,
            new InsightSubject("Product", product.ProductId, product.Name),
            [
                InsightFact.Text("sku", product.Sku),
                InsightFact.Number("quantity_sold", item.Quantity, unit),
                InsightFact.Number("qualifying_products", qualifying),
                InsightFact.Number("slowest_percent", SalesMetricsCalculator.SlowMoverPercent, "%"),
                InsightFact.Number("period_days", input.Current.Days, "days"),
                InsightFact.Text("period", Describe(input.Current)),
            ],
            item.Quantity == 0m
                ? $"{product.Name} had no sales in the last {input.Current.Days} days, among the slowest {SalesMetricsCalculator.SlowMoverPercent}% of {qualifying} active products."
                : $"{product.Name} sold only {Format(item.Quantity)} {unit} in the last {input.Current.Days} days, among the slowest {SalesMetricsCalculator.SlowMoverPercent}% of {qualifying} active products.",
            new InsightAction(ReviewAction, $"Review {product.Name}"));
    }

    private static string Describe(InsightWindow window) => $"{window.FirstDay:yyyy-MM-dd} to {window.LastDay:yyyy-MM-dd} ({window.TimeZoneId})";

    private static string Format(decimal value) => InsightFact.Number("x", value).Value;
}
