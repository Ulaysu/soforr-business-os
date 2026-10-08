namespace Soforr.Application.Intelligence.Rules;

/// <summary>
/// One line of a purchase order that has been placed: what was bought, from whom, when the order was placed, and at what unit cost.
/// A unit cost of 0 means no cost was recorded.
/// </summary>
public sealed record PurchasedLine(Guid OrderId, Guid ProductId, Guid SupplierId, DateTimeOffset PlacedAtUtc, decimal Quantity, decimal UnitCost, decimal LineTotal);

public sealed record PurchaseSupplier(Guid SupplierId, string Name);

/// <summary>One purchase of a product from a supplier: the order, when it was placed and the unit cost.</summary>
public sealed record PurchasePoint(Guid OrderId, DateTimeOffset PlacedAtUtc, decimal UnitCost);

/// <summary>
/// A supplier's latest costed purchase of a product against the purchase immediately before it from the same supplier. With only one
/// costed purchase there is no <see cref="Previous"/> and no change: that is insufficient history, not "no change".
/// <see cref="ChangePercent"/> is rounded to 1 decimal place; <see cref="Notable"/> is decided on the exact change.
/// </summary>
public sealed record SupplierCost(Guid SupplierId, string SupplierName, PurchasePoint Latest, PurchasePoint? Previous, decimal? ChangePercent, bool Notable);

/// <summary>The most and the least expensive supplier of a product, by each one's latest costed purchase.</summary>
public sealed record SupplierGap(SupplierCost Cheapest, SupplierCost MostExpensive, decimal GapPercent, bool Notable);

/// <summary>What was ordered in the recent window: orders, quantity and spend.</summary>
public sealed record PurchaseVolume(int Orders, decimal Quantity, decimal Spend);

public sealed record ProductPurchaseCost(
    CatalogProduct Product,
    IReadOnlyList<SupplierCost> Suppliers,
    SupplierGap? Gap,
    PurchaseVolume Volume,
    DateTimeOffset? LastPurchasedAtUtc,
    int PurchasesWithoutCost);

public sealed record PurchaseCosts(IReadOnlyList<ProductPurchaseCost> Products);

/// <summary>
/// <b>Purchase cost.</b> A pure calculation over the lines of placed purchase orders (drafts and cancelled orders are not purchases).
/// A purchase is dated when its order was placed and costed at the order line's unit cost.
/// <para>
/// <b>Change.</b> Per product and supplier, the latest costed purchase against the one immediately before it from the same supplier:
/// change % = (latest − previous) ÷ previous × 100, rounded to 1 decimal place half away from zero. A change is <b>notable</b> when
/// it is at least <see cref="NotableChangeFraction"/> (10%) of the previous cost, in either direction, decided on the exact values.
/// Purchases with no recorded cost (0) are left out of the comparison and counted separately.
/// </para>
/// <para>
/// <b>Gap.</b> With two or more suppliers that have a costed purchase, the gap is (most expensive latest cost − cheapest latest cost)
/// ÷ cheapest × 100, notable at the same 10%.
/// </para>
/// <para>
/// <b>Volume.</b> Orders, quantity and spend of the lines placed in the recent window. Ties on the placed time are broken by order id,
/// so the result does not depend on the input order.
/// </para>
/// </summary>
public static class PurchaseCostCalculator
{
    /// <summary>A change (or gap) of at least this fraction of the lower or earlier cost is notable.</summary>
    public const decimal NotableChangeFraction = 0.10m;

    public static PurchaseCosts Calculate(IReadOnlyCollection<PurchasedLine> lines, IReadOnlyCollection<PurchaseSupplier> suppliers, IReadOnlyCollection<CatalogProduct> products, InsightWindow recent)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(suppliers);
        ArgumentNullException.ThrowIfNull(products);
        ArgumentNullException.ThrowIfNull(recent);

        var names = suppliers.ToDictionary(supplier => supplier.SupplierId, supplier => supplier.Name);
        var byProduct = lines.GroupBy(line => line.ProductId).ToDictionary(group => group.Key, group => group.ToList());

        var result = new List<ProductPurchaseCost>();
        foreach (var product in products.Where(product => byProduct.ContainsKey(product.ProductId)))
        {
            var productLines = byProduct[product.ProductId];
            var costs = productLines
                .Where(line => line.UnitCost > 0m)
                .GroupBy(line => line.SupplierId)
                .Select(group => ForSupplier(group.Key, names.GetValueOrDefault(group.Key, string.Empty), group))
                .OrderBy(cost => cost.SupplierName, StringComparer.Ordinal)
                .ThenBy(cost => cost.SupplierId)
                .ToList();

            var inWindow = productLines.Where(line => line.PlacedAtUtc >= recent.StartUtc && line.PlacedAtUtc < recent.EndUtc).ToList();
            result.Add(new ProductPurchaseCost(
                product,
                costs,
                GapOf(costs),
                new PurchaseVolume(inWindow.Select(line => line.OrderId).Distinct().Count(), inWindow.Sum(line => line.Quantity), inWindow.Sum(line => line.LineTotal)),
                productLines.Max(line => line.PlacedAtUtc),
                productLines.Count(line => line.UnitCost == 0m)));
        }

        return new PurchaseCosts(result
            .OrderByDescending(product => product.Volume.Spend)
            .ThenByDescending(product => product.LastPurchasedAtUtc)
            .ThenBy(product => product.Product.Name, StringComparer.Ordinal)
            .ThenBy(product => product.Product.ProductId)
            .ToList());
    }

    public static bool IsNotable(decimal from, decimal to) => from > 0m && Math.Abs(to - from) >= from * NotableChangeFraction;

    private static SupplierCost ForSupplier(Guid supplierId, string name, IEnumerable<PurchasedLine> lines)
    {
        var ordered = lines.OrderByDescending(line => line.PlacedAtUtc).ThenByDescending(line => line.OrderId).ToList();
        var latest = new PurchasePoint(ordered[0].OrderId, ordered[0].PlacedAtUtc, ordered[0].UnitCost);
        if (ordered.Count < 2)
        {
            return new SupplierCost(supplierId, name, latest, null, null, false);
        }

        var previous = new PurchasePoint(ordered[1].OrderId, ordered[1].PlacedAtUtc, ordered[1].UnitCost);
        var change = Math.Round((latest.UnitCost - previous.UnitCost) / previous.UnitCost * 100m, 1, MidpointRounding.AwayFromZero);

        return new SupplierCost(supplierId, name, latest, previous, change, IsNotable(previous.UnitCost, latest.UnitCost));
    }

    private static SupplierGap? GapOf(List<SupplierCost> costs)
    {
        if (costs.Count < 2)
        {
            return null;
        }

        var cheapest = costs.OrderBy(cost => cost.Latest.UnitCost).ThenBy(cost => cost.SupplierName, StringComparer.Ordinal).ThenBy(cost => cost.SupplierId).First();
        var dearest = costs.OrderByDescending(cost => cost.Latest.UnitCost).ThenBy(cost => cost.SupplierName, StringComparer.Ordinal).ThenBy(cost => cost.SupplierId).First();
        var gap = Math.Round((dearest.Latest.UnitCost - cheapest.Latest.UnitCost) / cheapest.Latest.UnitCost * 100m, 1, MidpointRounding.AwayFromZero);

        return new SupplierGap(cheapest, dearest, gap, IsNotable(cheapest.Latest.UnitCost, dearest.Latest.UnitCost));
    }
}
