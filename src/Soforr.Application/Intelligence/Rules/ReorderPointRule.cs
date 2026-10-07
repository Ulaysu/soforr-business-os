using Soforr.Application.Inventory.ListLowStock;

namespace Soforr.Application.Intelligence.Rules;

/// <summary>A product and its stock, as the rule sees them. <see cref="Quantity"/> is 0 for a product that has never had stock.</summary>
public sealed record ProductStockSnapshot(
    Guid ProductId,
    string Sku,
    string Name,
    string UnitOfMeasure,
    bool IsActive,
    decimal Quantity,
    decimal? ReorderPoint,
    int? ReorderLeadTimeDays);

/// <summary>
/// <b>Reorder point reached.</b> A pure rule: a typed input and no I/O, so it is tested with fixtures.
/// <para>
/// <b>Formula.</b> A product is low when it is active, has reorder settings, and its stock is at or below its reorder point
/// (the single definition <see cref="LowStock.IsLow"/>, shared with the stock level and the low-stock list so they can never
/// disagree). There is no threshold to configure: the reorder point is the owner's own, per product.
/// </para>
/// <para>
/// <b>Severity.</b> <c>High</c> when there is no stock at all (nothing left to sell), <c>Medium</c> when there is some stock
/// left at or below the reorder point.
/// </para>
/// <para>
/// <b>Edge cases.</b> A product that has never had stock counts as 0. Inactive products and products without reorder
/// settings never produce an insight. The result is ordered by severity (high first), then the emptiest stock, then name,
/// then id. INTEL-002 builds on this with stock coverage and suggested reorder quantities.
/// </para>
/// </summary>
public static class ReorderPointRule
{
    public const string Type = "inventory.reorder_point_reached";

    public const string OrderMoreAction = "create_purchase_order";

    public static IReadOnlyList<Insight> Evaluate(IReadOnlyCollection<ProductStockSnapshot> products)
    {
        ArgumentNullException.ThrowIfNull(products);

        return products
            .Where(product => LowStock.IsLow(product.IsActive, product.ReorderPoint, product.Quantity))
            .OrderBy(product => product.Quantity == 0m ? 0 : 1)
            .ThenBy(product => product.Quantity)
            .ThenBy(product => product.Name, StringComparer.Ordinal)
            .ThenBy(product => product.ProductId)
            .Select(ToInsight)
            .ToList();
    }

    private static Insight ToInsight(ProductStockSnapshot product)
    {
        var outOfStock = product.Quantity == 0m;
        var facts = new List<InsightFact>
        {
            InsightFact.Text("sku", product.Sku),
            InsightFact.Number("stock", product.Quantity, product.UnitOfMeasure),
            InsightFact.Number("reorder_point", product.ReorderPoint!.Value, product.UnitOfMeasure),
        };
        if (product.ReorderLeadTimeDays is { } leadTime)
        {
            facts.Add(InsightFact.Number("lead_time_days", leadTime, "days"));
        }

        var reason = outOfStock
            ? $"{product.Name} is out of stock (reorder point {InsightFact.Number("x", product.ReorderPoint.Value).Value} {product.UnitOfMeasure})."
            : $"{product.Name} is at or below its reorder point: {InsightFact.Number("x", product.Quantity).Value} {product.UnitOfMeasure} in stock, reorder point {InsightFact.Number("x", product.ReorderPoint.Value).Value}.";

        return Insight.Create(
            Type,
            outOfStock ? InsightSeverity.High : InsightSeverity.Medium,
            new InsightSubject("Product", product.ProductId, product.Name),
            facts,
            reason,
            new InsightAction(OrderMoreAction, $"Order more {product.Name}"));
    }
}
