namespace Soforr.Application.Intelligence.Rules;

/// <summary>
/// One line of a completed sale: what was sold, when, how much and what it was charged. <see cref="Revenue"/> is the line total
/// (final unit price × quantity, as the Domain computes it). <see cref="Cost"/> is the line cost from the cost price recorded on
/// the line at the time of the sale (never the product's current cost), and <see cref="CostRecorded"/> says whether a cost was
/// recorded at all: the catalog cannot say "unknown", so a cost price of 0 is how an owner who does not track cost leaves it, and
/// margin intelligence treats such a line as having no recorded cost instead of guessing.
/// </summary>
public sealed record SoldLine(Guid SaleId, Guid ProductId, DateTimeOffset SoldAtUtc, decimal Quantity, decimal Revenue, decimal Cost = 0m, bool CostRecorded = false);

/// <summary>A product as the calculator sees it. <see cref="CreatedAtUtc"/> decides whether it existed for a whole period.</summary>
public sealed record CatalogProduct(Guid ProductId, string Sku, string Name, string UnitOfMeasure, bool IsActive, DateTimeOffset CreatedAtUtc);

/// <summary>What one product sold in the current period and in the period before it.</summary>
public sealed record ProductSales(
    CatalogProduct Product,
    decimal Quantity,
    decimal Revenue,
    decimal PreviousQuantity,
    decimal PreviousRevenue);

/// <summary>A product whose quantity sold fell by at least the decline threshold between the two periods.</summary>
public sealed record DecliningProduct(ProductSales Sales, decimal DeclinePercent);

/// <summary>
/// The slowest sellers. <see cref="ShowsFullList"/> is true when fewer than <see cref="SalesMetricsCalculator.SlowMoverMinimumProducts"/>
/// products qualify: then every qualifying product is listed in order instead of a bottom share.
/// </summary>
public sealed record SlowMovers(int QualifyingProducts, bool ShowsFullList, IReadOnlyList<ProductSales> Items);

/// <summary>Revenue and sales count for the two periods. <see cref="RevenueGrowthPercent"/> is null when there is no comparable previous revenue.</summary>
public sealed record SalesTotals(decimal Revenue, decimal PreviousRevenue, int SaleCount, int PreviousSaleCount, decimal? RevenueGrowthPercent);

public sealed record SalesMetrics(
    SalesTotals Totals,
    IReadOnlyList<ProductSales> TopSellers,
    SlowMovers SlowMovers,
    IReadOnlyList<DecliningProduct> DecliningProducts);

/// <summary>
/// <b>Sales metrics and comparisons.</b> A pure calculator: lines, products and two periods in, metrics out; no I/O.
/// <para>
/// <b>Periods.</b> The <i>current</i> period and the <i>previous</i> period (the same number of whole local days,
/// immediately before it), each half-open <c>[start, end)</c> in the organization's time zone (see
/// <see cref="InsightWindows"/>). A line belongs to the period containing the sale's time. The caller supplies only
/// <b>completed</b> sales: a voided sale is not a sale, so it counts in neither period.
/// </para>
/// <para>
/// <b>Formulas.</b> Revenue of a period is the sum of its line totals; the sale count is the number of distinct sales. Revenue
/// growth is <c>(current − previous) ÷ previous × 100</c> when previous revenue is not zero; otherwise there is no growth figure
/// ("no comparable previous sales"), never zero or infinity. Quantity is reported per product, never summed across products (a
/// bag and a kilogram are not comparable). Percentages are rounded to 1 decimal place, half away from zero, once at the end;
/// comparisons use the unrounded values.
/// </para>
/// <para>
/// <b>Top sellers</b> are ordered by quantity sold in the current period (most first), then revenue (most first), then name,
/// then id; only products that sold something in the current period are listed.
/// </para>
/// <para>
/// <b>Declining</b>: a product with sales in <i>both</i> periods whose quantity fell by at least
/// <see cref="DecliningThresholdPercent"/> percent (<c>current ≤ 80% of previous</c>), the largest decline first.
/// </para>
/// <para>
/// <b>Slow movers</b>: of the <i>qualifying</i> products (active, and created before the current period began, so they had the
/// whole period to sell) ordered by quantity sold in the current period (least first, a product that sold nothing has 0), then
/// name, then id, the bottom <see cref="SlowMoverPercent"/> percent, <c>max(1, floor(n × 20%))</c> products. When fewer than
/// <see cref="SlowMoverMinimumProducts"/> products qualify, the whole ordered list is shown instead.
/// </para>
/// </summary>
public static class SalesMetricsCalculator
{
    public const decimal DecliningThresholdPercent = 20m;

    public const int SlowMoverPercent = 20;

    public const int SlowMoverMinimumProducts = 5;

    public static SalesMetrics Calculate(
        IReadOnlyCollection<SoldLine> lines,
        IReadOnlyCollection<CatalogProduct> products,
        InsightWindow current,
        InsightWindow previous,
        int topCount)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(products);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentOutOfRangeException.ThrowIfLessThan(topCount, 1);

        var inCurrent = lines.Where(line => line.SoldAtUtc >= current.StartUtc && line.SoldAtUtc < current.EndUtc).ToList();
        var inPrevious = lines.Where(line => line.SoldAtUtc >= previous.StartUtc && line.SoldAtUtc < previous.EndUtc).ToList();

        var currentByProduct = inCurrent.GroupBy(line => line.ProductId).ToDictionary(group => group.Key, group => (Quantity: group.Sum(l => l.Quantity), Revenue: group.Sum(l => l.Revenue)));
        var previousByProduct = inPrevious.GroupBy(line => line.ProductId).ToDictionary(group => group.Key, group => (Quantity: group.Sum(l => l.Quantity), Revenue: group.Sum(l => l.Revenue)));

        var sales = products
            .Select(product =>
            {
                var now = currentByProduct.GetValueOrDefault(product.ProductId);
                var before = previousByProduct.GetValueOrDefault(product.ProductId);
                return new ProductSales(product, now.Quantity, now.Revenue, before.Quantity, before.Revenue);
            })
            .ToList();

        var revenue = inCurrent.Sum(line => line.Revenue);
        var previousRevenue = inPrevious.Sum(line => line.Revenue);
        var totals = new SalesTotals(
            revenue,
            previousRevenue,
            inCurrent.Select(line => line.SaleId).Distinct().Count(),
            inPrevious.Select(line => line.SaleId).Distinct().Count(),
            previousRevenue != 0m ? Round((revenue - previousRevenue) / previousRevenue * 100m) : null);

        var topSellers = sales
            .Where(item => item.Quantity > 0m)
            .OrderByDescending(item => item.Quantity)
            .ThenByDescending(item => item.Revenue)
            .ThenBy(item => item.Product.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Product.ProductId)
            .Take(topCount)
            .ToList();

        var declining = sales
            .Where(item => item.Quantity > 0m && item.PreviousQuantity > 0m && (item.PreviousQuantity - item.Quantity) * 100m >= item.PreviousQuantity * DecliningThresholdPercent)
            .Select(item => new DecliningProduct(item, Round((item.PreviousQuantity - item.Quantity) / item.PreviousQuantity * 100m)))
            .OrderByDescending(item => (item.Sales.PreviousQuantity - item.Sales.Quantity) / item.Sales.PreviousQuantity)
            .ThenBy(item => item.Sales.Product.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Sales.Product.ProductId)
            .ToList();

        var qualifying = sales
            .Where(item => item.Product.IsActive && item.Product.CreatedAtUtc < current.StartUtc)
            .OrderBy(item => item.Quantity)
            .ThenBy(item => item.Product.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Product.ProductId)
            .ToList();

        var slowMovers = qualifying.Count < SlowMoverMinimumProducts
            ? new SlowMovers(qualifying.Count, ShowsFullList: true, qualifying)
            : new SlowMovers(qualifying.Count, ShowsFullList: false, qualifying.Take(Math.Max(1, qualifying.Count * SlowMoverPercent / 100)).ToList());

        return new SalesMetrics(totals, topSellers, slowMovers, declining);
    }

    private static decimal Round(decimal value) => decimal.Round(value, 1, MidpointRounding.AwayFromZero);
}
