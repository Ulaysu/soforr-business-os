using Soforr.Application.Inventory.ListLowStock;

namespace Soforr.Application.Intelligence.Rules;

/// <summary>
/// A product, its stock and what it sold in the window, as the rule sees them. <see cref="Quantity"/> is 0 for a product
/// that has never had stock. <see cref="SoldInWindow"/> is the quantity on completed (not voided) sales in the window.
/// </summary>
public sealed record ProductStockSnapshot(
    Guid ProductId,
    string Sku,
    string Name,
    string UnitOfMeasure,
    bool IsActive,
    decimal Quantity,
    decimal? ReorderPoint,
    int? ReorderLeadTimeDays,
    decimal SoldInWindow = 0m);

/// <summary>
/// <b>Inventory risk.</b> A pure rule: typed input and the window, no I/O, so it is tested with fixtures and a fixed clock.
/// <para>
/// <b>Window.</b> The <see cref="DefaultWindowDays"/> complete local calendar days before today in the organization's time
/// zone (<see cref="InsightWindows.TrailingCompleteDays"/>); only completed sales count (a voided sale is not a sale).
/// </para>
/// <para>
/// <b>Formulas</b> (<c>S</c> stock, <c>R</c> reorder point, <c>L</c> lead time in days, <c>Q</c> quantity sold in the window,
/// <c>N</c> days in the window):
/// average daily sales <c>A = Q ÷ N</c>; <b>coverage</b> in days <c>= S ÷ A</c> when <c>Q &gt; 0</c>; with no sales in the
/// window there is no coverage: it is reported as "no recent sales", never as infinity or as a number;
/// <b>suggested order quantity</b> <c>= max(0, R + A × L − S)</c>, rounded to 4 decimal places (the quantity scale), half
/// away from zero, and given only when the product has a reorder point and a lead time. A suggestion is never computed from a
/// missing input.
/// </para>
/// <para>
/// <b>What is reported</b> (at most one insight per product): the product is <i>at its reorder point</i> when it is low
/// (<see cref="LowStock.IsLow"/>, the one definition shared with the stock level and the low-stock list), giving
/// <c>inventory.reorder_point_reached</c>; otherwise it <i>runs out before a new order could arrive</i> when it has sales,
/// a lead time, and coverage is shorter than the lead time (<c>S ÷ A &lt; L</c>), giving <c>inventory.runs_out_before_restock</c>.
/// </para>
/// <para>
/// <b>Severity.</b> <c>High</c> when there is no stock at all, or coverage is shorter than the lead time (it will run out
/// before an order placed now arrives); <c>Medium</c> when the reorder point is reached but coverage still outlasts the lead
/// time or there are no recent sales.
/// </para>
/// <para>
/// <b>Edge cases.</b> A product that has never had stock counts as 0. Inactive products and products without reorder settings
/// never produce an insight. No history or zero velocity means no coverage and no run-out insight (only the reorder point can
/// raise one). Zero stock with sales has coverage 0 days. Facts show coverage to 1 decimal place and average daily sales to 2,
/// half away from zero; comparisons use the unrounded values. The result is ordered by severity (high first), then stock
/// (emptiest first), then name, then id.
/// </para>
/// </summary>
public static class InventoryRiskRule
{
    public const string ReorderPointReachedType = "inventory.reorder_point_reached";

    public const string RunsOutBeforeRestockType = "inventory.runs_out_before_restock";

    public const string OrderMoreAction = "create_purchase_order";

    /// <summary>The default window: 30 complete local calendar days.</summary>
    public const int DefaultWindowDays = 30;

    public static IReadOnlyList<Insight> Evaluate(IReadOnlyCollection<ProductStockSnapshot> products, InsightWindow window)
    {
        ArgumentNullException.ThrowIfNull(products);
        ArgumentNullException.ThrowIfNull(window);

        return products
            .Select(product => Assess(product, window))
            .Where(assessment => assessment is not null)
            .Select(assessment => assessment!)
            .OrderByDescending(assessment => assessment.Severity)
            .ThenBy(assessment => assessment.Product.Quantity)
            .ThenBy(assessment => assessment.Product.Name, StringComparer.Ordinal)
            .ThenBy(assessment => assessment.Product.ProductId)
            .Select(assessment => assessment.ToInsight(window))
            .ToList();
    }

    private static Assessment? Assess(ProductStockSnapshot product, InsightWindow window)
    {
        if (!product.IsActive || product.ReorderPoint is not { } reorderPoint)
        {
            return null;
        }

        var average = product.SoldInWindow / window.Days;
        decimal? coverage = product.SoldInWindow > 0m ? product.Quantity * window.Days / product.SoldInWindow : null;
        var atReorderPoint = LowStock.IsLow(product.IsActive, reorderPoint, product.Quantity);
        var shorterThanLeadTime = coverage is { } days && product.ReorderLeadTimeDays is { } lead && days < lead;

        if (!atReorderPoint && !shorterThanLeadTime)
        {
            return null;
        }

        decimal? suggested = null;
        if (product.ReorderLeadTimeDays is { } leadTime)
        {
            var needed = decimal.Round(reorderPoint + average * leadTime - product.Quantity, 4, MidpointRounding.AwayFromZero);
            suggested = Math.Max(0m, needed);
        }

        var severity = product.Quantity == 0m || shorterThanLeadTime ? InsightSeverity.High : InsightSeverity.Medium;

        return new Assessment(product, reorderPoint, average, coverage, atReorderPoint, shorterThanLeadTime, suggested, severity);
    }

    private sealed record Assessment(
        ProductStockSnapshot Product,
        decimal ReorderPoint,
        decimal AverageDailySales,
        decimal? Coverage,
        bool AtReorderPoint,
        bool ShorterThanLeadTime,
        decimal? Suggested,
        InsightSeverity Severity)
    {
        public Insight ToInsight(InsightWindow window)
        {
            var product = Product;
            var unit = product.UnitOfMeasure;
            var facts = new List<InsightFact>
            {
                InsightFact.Text("sku", product.Sku),
                InsightFact.Number("stock", product.Quantity, unit),
                InsightFact.Number("reorder_point", ReorderPoint, unit),
            };
            if (product.ReorderLeadTimeDays is { } lead)
            {
                facts.Add(InsightFact.Number("lead_time_days", lead, "days"));
            }

            facts.Add(InsightFact.Number("sold_in_window", product.SoldInWindow, unit));
            facts.Add(InsightFact.Number("window_days", window.Days, "days"));
            facts.Add(InsightFact.Text("window", $"{window.FirstDay:yyyy-MM-dd} to {window.LastDay:yyyy-MM-dd} ({window.TimeZoneId})"));
            facts.Add(InsightFact.Number("average_daily_sales", Round(AverageDailySales, 2), unit + "/day"));
            if (Coverage is { } coverage)
            {
                facts.Add(InsightFact.Number("coverage_days", Round(coverage, 1), "days"));
            }
            else
            {
                facts.Add(InsightFact.Text("coverage", "no recent sales"));
            }

            if (Suggested is > 0m)
            {
                facts.Add(InsightFact.Number("suggested_order_quantity", Suggested.Value, unit));
            }

            var type = AtReorderPoint ? ReorderPointReachedType : RunsOutBeforeRestockType;
            return Insight.Create(
                type,
                Severity,
                new InsightSubject("Product", product.ProductId, product.Name),
                facts,
                Reason(window),
                new InsightAction(OrderMoreAction, $"Order more {product.Name}"));
        }

        private string Reason(InsightWindow window)
        {
            var product = Product;
            var unit = product.UnitOfMeasure;
            var stock = Format(product.Quantity);

            var velocity = Coverage is { } coverage
                ? $" At about {Format(Round(AverageDailySales, 2))} {unit}/day over the last {window.Days} days it lasts about {Format(Round(coverage, 1))} days"
                  + (product.ReorderLeadTimeDays is { } lead ? $", and restocking takes {lead} days." : ".")
                : $" It has had no sales in the last {window.Days} days.";

            if (!AtReorderPoint)
            {
                return $"{product.Name} may run out before a new order could arrive: {stock} {unit} in stock.{velocity}";
            }

            return product.Quantity == 0m
                ? $"{product.Name} is out of stock (reorder point {Format(ReorderPoint)} {unit}).{velocity}"
                : $"{product.Name} is at or below its reorder point: {stock} {unit} in stock, reorder point {Format(ReorderPoint)}.{velocity}";
        }

        private static decimal Round(decimal value, int places) => decimal.Round(value, places, MidpointRounding.AwayFromZero);

        private static string Format(decimal value) => InsightFact.Number("x", value).Value;
    }
}
