namespace Soforr.Application.Intelligence.Rules;

/// <summary>
/// How much a period's margin can be trusted. <see cref="NoSales"/>: nothing was sold. <see cref="CostNotRecorded"/>: things
/// were sold but none of the lines recorded a cost, so there is no margin. <see cref="PartlyCosted"/>: some lines recorded no
/// cost; the margin is computed on the lines that did and the rest is reported separately. <see cref="Ok"/>: every line has a cost.
/// </summary>
public enum MarginStatus
{
    NoSales = 1,
    CostNotRecorded = 2,
    PartlyCosted = 3,
    Ok = 4,
}

/// <summary>
/// The margin of one period, over the lines that recorded a cost. <see cref="Revenue"/>, <see cref="Cost"/> and
/// <see cref="Quantity"/> are those of the costed lines only, so the margin is never inflated by lines whose cost is unknown;
/// <see cref="RevenueWithoutCost"/> and <see cref="QuantityWithoutCost"/> are the rest. <see cref="MarginPercent"/> is null when
/// there is no revenue to divide by; the averages are null when nothing was sold with a cost.
/// </summary>
public sealed record MarginFigures(
    decimal Revenue,
    decimal Cost,
    decimal GrossMargin,
    decimal? MarginPercent,
    decimal Quantity,
    decimal? AverageUnitPrice,
    decimal? AverageUnitCost,
    decimal RevenueWithoutCost,
    decimal QuantityWithoutCost,
    MarginStatus Status);

/// <summary>
/// One product's margin in the current period and in the period before it. <see cref="MarginChangePoints"/> is the change in the
/// margin percentage in percentage points (current minus previous), null unless both periods have a margin percentage;
/// <see cref="ExactMarginChangePoints"/> is the same figure before rounding, which is what comparisons use.
/// </summary>
public sealed record ProductMargin(
    CatalogProduct Product,
    MarginFigures Current,
    MarginFigures Previous,
    decimal? MarginChangePoints,
    decimal? ExactMarginChangePoints);

/// <summary>The margin of all sales in the two periods, with the same figures as one product's.</summary>
public sealed record MarginTotals(MarginFigures Current, MarginFigures Previous, decimal? MarginChangePoints);

public sealed record MarginMetrics(MarginTotals Totals, IReadOnlyList<ProductMargin> Products);

/// <summary>
/// <b>Product margin intelligence.</b> A pure calculator over the lines of completed sales, the catalog and two periods
/// (see <see cref="SalesMetricsCalculator"/> and <see cref="InsightWindows"/>: whole local days in the organization's time zone,
/// half-open, the previous period of equal length immediately before; a voided sale counts in neither).
/// <para>
/// <b>Management margin, not accounting.</b> Gross margin = revenue − cost, where revenue is the final price actually charged and
/// cost is the cost price <i>recorded on the sale line at the time of the sale</i> (never the product's current price or cost).
/// There is no FIFO, weighted-average costing or inventory valuation. Margin percentage = gross margin ÷ revenue × 100, to 1
/// decimal place half away from zero, when revenue is not zero (otherwise there is none). The change is in
/// <b>percentage points</b>: current margin percentage minus previous, to 1 decimal place, computed from the unrounded values.
/// </para>
/// <para>
/// <b>Missing data is never guessed.</b> The catalog cannot say "unknown cost", and 0 is the only way an owner who does not track
/// cost can leave it, so a line recorded with a cost price of 0 counts as having <i>no recorded cost</i>. Such lines are left out of
/// the margin (cost, revenue and quantity of the margin are those of the costed lines) and reported as revenue without cost, so
/// the margin is never flattered by them; a period with sales but no costed line has no margin
/// (<see cref="MarginStatus.CostNotRecorded"/>), and a period with no sales has none (<see cref="MarginStatus.NoSales"/>). A change
/// is reported only when both periods have a margin percentage.
/// </para>
/// <para>
/// <b>Averages</b> (unit price and unit cost) are over the costed lines: revenue ÷ quantity and cost ÷ quantity, to 4 decimal
/// places half away from zero. They exist so an explanation can show previous and current price and cost side by side.
/// </para>
/// <para>
/// Products are returned when they sold anything in either period, ordered by the current gross margin (largest first), then
/// name, then id.
/// </para>
/// </summary>
public static class MarginCalculator
{
    /// <summary>A product's margin has declined when its margin percentage fell by at least this many percentage points. This is the one threshold of the declining-margin insight.</summary>
    public const decimal DeclineThresholdPoints = 5m;

    public static MarginMetrics Calculate(
        IReadOnlyCollection<SoldLine> lines,
        IReadOnlyCollection<CatalogProduct> products,
        InsightWindow current,
        InsightWindow previous)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(products);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous);

        var inCurrent = lines.Where(line => line.SoldAtUtc >= current.StartUtc && line.SoldAtUtc < current.EndUtc).ToList();
        var inPrevious = lines.Where(line => line.SoldAtUtc >= previous.StartUtc && line.SoldAtUtc < previous.EndUtc).ToList();

        var totals = new MarginTotals(
            Figures(inCurrent, out var currentExact),
            Figures(inPrevious, out var previousExact),
            Change(currentExact, previousExact));

        var currentByProduct = inCurrent.ToLookup(line => line.ProductId);
        var previousByProduct = inPrevious.ToLookup(line => line.ProductId);

        var margins = products
            .Where(product => currentByProduct.Contains(product.ProductId) || previousByProduct.Contains(product.ProductId))
            .Select(product =>
            {
                var now = Figures(currentByProduct[product.ProductId].ToList(), out var nowExact);
                var before = Figures(previousByProduct[product.ProductId].ToList(), out var beforeExact);
                var exactChange = nowExact is { } a && beforeExact is { } b ? a - b : (decimal?)null;

                return new ProductMargin(product, now, before, exactChange is { } exact ? Round(exact, 1) : null, exactChange);
            })
            .OrderByDescending(margin => margin.Current.GrossMargin)
            .ThenBy(margin => margin.Product.Name, StringComparer.Ordinal)
            .ThenBy(margin => margin.Product.ProductId)
            .ToList();

        return new MarginMetrics(totals, margins);
    }

    /// <summary>Whether a product's margin percentage fell by at least <see cref="DeclineThresholdPoints"/> points, compared unrounded.</summary>
    public static bool HasDeclined(ProductMargin margin)
    {
        ArgumentNullException.ThrowIfNull(margin);

        return margin.ExactMarginChangePoints is { } change && change <= -DeclineThresholdPoints;
    }

    private static MarginFigures Figures(List<SoldLine> lines, out decimal? exactPercent)
    {
        if (lines.Count == 0)
        {
            exactPercent = null;
            return new MarginFigures(0m, 0m, 0m, null, 0m, null, null, 0m, 0m, MarginStatus.NoSales);
        }

        var costed = lines.Where(line => line.CostRecorded).ToList();
        var uncosted = lines.Where(line => !line.CostRecorded).ToList();
        var revenue = costed.Sum(line => line.Revenue);
        var cost = costed.Sum(line => line.Cost);
        var quantity = costed.Sum(line => line.Quantity);
        var margin = revenue - cost;

        exactPercent = revenue != 0m ? margin / revenue * 100m : null;

        return new MarginFigures(
            revenue,
            cost,
            margin,
            exactPercent is { } percent ? Round(percent, 1) : null,
            quantity,
            quantity != 0m ? Round(revenue / quantity, 4) : null,
            quantity != 0m ? Round(cost / quantity, 4) : null,
            uncosted.Sum(line => line.Revenue),
            uncosted.Sum(line => line.Quantity),
            costed.Count == 0 ? MarginStatus.CostNotRecorded : uncosted.Count == 0 ? MarginStatus.Ok : MarginStatus.PartlyCosted);
    }

    private static decimal? Change(decimal? currentExact, decimal? previousExact) =>
        currentExact is { } a && previousExact is { } b ? Round(a - b, 1) : null;

    private static decimal Round(decimal value, int places) => decimal.Round(value, places, MidpointRounding.AwayFromZero);
}
