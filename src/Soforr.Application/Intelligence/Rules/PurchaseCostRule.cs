using System.Globalization;

namespace Soforr.Application.Intelligence.Rules;

/// <summary>
/// <b>Purchase cost insights.</b> A pure rule over <see cref="PurchaseCosts"/>.
/// <para>
/// <b>Cost change</b> (<see cref="CostChangeType"/>): a product gets one insight when a supplier's latest costed purchase, placed on or
/// after <c>recentFrom</c>, is a <b>notable</b> change (at least 10% of the previous cost, see <see cref="PurchaseCostCalculator"/>) on that supplier's
/// previous purchase. <c>Medium</c> if any notable change is an increase; <c>Info</c> if all are decreases. Each such supplier is named with
/// both costs and dates.
/// </para>
/// <para>
/// <b>Supplier gap</b> (<see cref="SupplierGapType"/>, <c>Low</c>): a product bought from two or more suppliers, with a purchase on or after
/// <c>recentFrom</c>, whose latest costs differ by a notable gap. The cheapest and the most expensive supplier are named.
/// </para>
/// A product with a single costed purchase, or no recorded cost, has no change and no insight: that is insufficient history, never a guess.
/// Ordered by severity (high first), then name, then id.
/// </summary>
public static class PurchaseCostRule
{
    public const string CostChangeType = "purchasing.cost_change";

    public const string SupplierGapType = "purchasing.supplier_cost_gap";

    public const string ReviewAction = "review_supplier_costs";

    /// <summary>How many supplier changes are named in one insight.</summary>
    public const int ListedSuppliers = 3;

    public static IReadOnlyList<Insight> Evaluate(PurchaseCosts costs, DateTimeOffset recentFrom)
    {
        ArgumentNullException.ThrowIfNull(costs);

        var insights = new List<Insight>();
        foreach (var product in costs.Products)
        {
            var changes = product.Suppliers
                .Where(cost => cost.Notable && cost.Latest.PlacedAtUtc >= recentFrom)
                .OrderByDescending(cost => Math.Abs(cost.ChangePercent!.Value))
                .ThenBy(cost => cost.SupplierName, StringComparer.Ordinal)
                .ThenBy(cost => cost.SupplierId)
                .ToList();
            if (changes.Count > 0)
            {
                insights.Add(CostChange(product, changes));
            }

            if (product.Gap is { Notable: true } gap && product.Suppliers.Any(cost => cost.Latest.PlacedAtUtc >= recentFrom))
            {
                insights.Add(Gap(product, gap));
            }
        }

        return insights
            .OrderByDescending(insight => insight.Severity)
            .ThenBy(insight => insight.Subject.Name, StringComparer.Ordinal)
            .ThenBy(insight => insight.Subject.Id)
            .ThenBy(insight => insight.Type, StringComparer.Ordinal)
            .ToList();
    }

    private static Insight CostChange(ProductPurchaseCost product, List<SupplierCost> changes)
    {
        var increased = changes.Any(cost => cost.ChangePercent > 0m);
        var facts = new List<InsightFact> { InsightFact.Text("sku", product.Product.Sku), InsightFact.Number("notable_change_threshold", PurchaseCostCalculator.NotableChangeFraction * 100m, "%") };

        for (var index = 0; index < Math.Min(changes.Count, ListedSuppliers); index++)
        {
            var cost = changes[index];
            facts.Add(InsightFact.Text($"supplier_change_{index + 1}", string.Create(CultureInfo.InvariantCulture, $"{cost.SupplierName} | {Format(cost.Previous!.UnitCost)} on {cost.Previous.PlacedAtUtc:yyyy-MM-dd} to {Format(cost.Latest.UnitCost)} on {cost.Latest.PlacedAtUtc:yyyy-MM-dd} | {Signed(cost.ChangePercent!.Value)}%")));
        }

        if (changes.Count > ListedSuppliers)
        {
            facts.Add(InsightFact.Number("supplier_changes_not_listed", changes.Count - ListedSuppliers));
        }

        var lead = changes[0];
        var direction = lead.ChangePercent > 0m ? "rose" : "fell";
        var reason = $"The cost of {product.Product.Name} from {lead.SupplierName} {direction} from {Format(lead.Previous!.UnitCost)} to {Format(lead.Latest.UnitCost)} ({Signed(lead.ChangePercent!.Value)}%) between the purchases of {lead.Previous.PlacedAtUtc:yyyy-MM-dd} and {lead.Latest.PlacedAtUtc:yyyy-MM-dd}"
            + (changes.Count > 1 ? $"; {changes.Count - 1} more supplier{(changes.Count > 2 ? "s" : string.Empty)} changed notably too." : ".");

        return Insight.Create(
            CostChangeType,
            increased ? InsightSeverity.Medium : InsightSeverity.Info,
            new InsightSubject("Product", product.Product.ProductId, product.Product.Name),
            facts,
            reason,
            new InsightAction(ReviewAction, $"Review the cost of {product.Product.Name}"));
    }

    private static Insight Gap(ProductPurchaseCost product, SupplierGap gap)
    {
        var facts = new List<InsightFact>
        {
            InsightFact.Text("sku", product.Product.Sku),
            InsightFact.Text("cheapest_supplier", gap.Cheapest.SupplierName),
            InsightFact.Number("cheapest_unit_cost", gap.Cheapest.Latest.UnitCost),
            InsightFact.Text("cheapest_purchased_on", $"{gap.Cheapest.Latest.PlacedAtUtc:yyyy-MM-dd}"),
            InsightFact.Text("most_expensive_supplier", gap.MostExpensive.SupplierName),
            InsightFact.Number("most_expensive_unit_cost", gap.MostExpensive.Latest.UnitCost),
            InsightFact.Text("most_expensive_purchased_on", $"{gap.MostExpensive.Latest.PlacedAtUtc:yyyy-MM-dd}"),
            InsightFact.Number("gap_percent", gap.GapPercent, "%"),
            InsightFact.Number("suppliers_compared", product.Suppliers.Count),
            InsightFact.Number("notable_change_threshold", PurchaseCostCalculator.NotableChangeFraction * 100m, "%"),
        };

        return Insight.Create(
            SupplierGapType,
            InsightSeverity.Low,
            new InsightSubject("Product", product.Product.ProductId, product.Product.Name),
            facts,
            $"{gap.MostExpensive.SupplierName} charged {Format(gap.MostExpensive.Latest.UnitCost)} for {product.Product.Name} on its latest purchase, {Format(gap.GapPercent)}% more than {gap.Cheapest.SupplierName} at {Format(gap.Cheapest.Latest.UnitCost)}.",
            new InsightAction(ReviewAction, $"Review the cost of {product.Product.Name}"));
    }

    private static string Format(decimal value) => InsightFact.Number("x", value).Value;

    private static string Signed(decimal value) => (value > 0m ? "+" : string.Empty) + Format(value);
}
