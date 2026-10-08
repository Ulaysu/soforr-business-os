using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Intelligence.GetSalesSummary;
using Soforr.Application.Intelligence.Rules;
using Soforr.Application.Intelligence.Sources;
using Soforr.Application.Persistence;

namespace Soforr.Application.Intelligence.GetMarginSummary;

/// <summary>
/// Margins for the <see cref="Days"/> complete local days before today compared with the same number of days before those
/// (default <see cref="DefaultDays"/>, at most <see cref="MaxDays"/>), with the <see cref="Top"/> products of the largest gross margin
/// (default <see cref="DefaultTop"/>, at most <see cref="MaxTop"/>).
/// </summary>
public sealed record GetMarginSummaryQuery(int? Days, int? Top)
{
    public const int DefaultDays = GetSalesSummaryQuery.DefaultDays;

    public const int MaxDays = GetSalesSummaryQuery.MaxDays;

    public const int DefaultTop = 10;

    public const int MaxTop = 50;
}

/// <summary>
/// The margin of one period. Revenue, cost and quantity are those of the lines that recorded a cost; the rest is
/// <see cref="RevenueWithoutCost"/>. <see cref="Status"/> is <c>no_sales</c>, <c>cost_not_recorded</c> (sales without any recorded
/// cost: there is no margin), <c>partly_costed</c> or <c>ok</c>. The margin percentage is null when there is no revenue to divide by.
/// </summary>
public sealed record MarginFiguresDto(
    decimal Revenue,
    decimal Cost,
    decimal GrossMargin,
    decimal? MarginPercent,
    decimal Quantity,
    decimal? AverageUnitPrice,
    decimal? AverageUnitCost,
    decimal RevenueWithoutCost,
    decimal QuantityWithoutCost,
    string Status)
{
    internal static MarginFiguresDto From(MarginFigures figures) => new(
        figures.Revenue,
        figures.Cost,
        figures.GrossMargin,
        figures.MarginPercent,
        figures.Quantity,
        figures.AverageUnitPrice,
        figures.AverageUnitCost,
        figures.RevenueWithoutCost,
        figures.QuantityWithoutCost,
        figures.Status switch
        {
            MarginStatus.NoSales => "no_sales",
            MarginStatus.CostNotRecorded => "cost_not_recorded",
            MarginStatus.PartlyCosted => "partly_costed",
            _ => "ok",
        });
}

/// <summary>A product's margin in both periods. <see cref="MarginChangePoints"/> is current minus previous margin percentage in percentage points, null unless both periods have one.</summary>
public sealed record ProductMarginDto(Guid ProductId, string Sku, string Name, string UnitOfMeasure, MarginFiguresDto Current, MarginFiguresDto Previous, decimal? MarginChangePoints)
{
    internal static ProductMarginDto From(ProductMargin margin) => new(
        margin.Product.ProductId,
        margin.Product.Sku,
        margin.Product.Name,
        margin.Product.UnitOfMeasure,
        MarginFiguresDto.From(margin.Current),
        MarginFiguresDto.From(margin.Previous),
        margin.MarginChangePoints);
}

public sealed record MarginTotalsDto(MarginFiguresDto Current, MarginFiguresDto Previous, decimal? MarginChangePoints);

public sealed record MarginSummaryDto(
    string Currency,
    string TimeZoneId,
    SalesPeriodDto Current,
    SalesPeriodDto Previous,
    MarginTotalsDto Totals,
    IReadOnlyList<ProductMarginDto> TopProducts,
    IReadOnlyList<ProductMarginDto> NegativeMargin,
    IReadOnlyList<ProductMarginDto> DecliningMargin,
    IReadOnlyList<ProductMarginDto> MissingCost);

/// <summary>
/// The margin metrics (<see cref="MarginCalculator"/>) for the caller's organization, computed on request from the cost prices
/// recorded on completed sale lines; nothing is stored. It shows costs, so it needs <c>ProductViewCost</c> as well as
/// <c>InsightsView</c>; a caller without either is refused, whatever else they may see.
/// </summary>
public sealed class GetMarginSummaryHandler(IAppDbContext db, IClock clock, ICurrentUser user) : IQueryHandler<GetMarginSummaryQuery, MarginSummaryDto>
{
    public async Task<Result<MarginSummaryDto>> HandleAsync(GetMarginSummaryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (user.Require(Permission.InsightsView) is { } denied)
        {
            return Result.Failure<MarginSummaryDto>(denied);
        }

        if (user.Require(Permission.ProductViewCost) is { } deniedCost)
        {
            return Result.Failure<MarginSummaryDto>(deniedCost);
        }

        var days = query.Days ?? GetMarginSummaryQuery.DefaultDays;
        if (days is < 1 or > GetMarginSummaryQuery.MaxDays)
        {
            return Result.Failure<MarginSummaryDto>(new Error("margin_summary.days_invalid", ErrorKind.Validation, $"The period must be between 1 and {GetMarginSummaryQuery.MaxDays} days.", "days"));
        }

        var top = query.Top ?? GetMarginSummaryQuery.DefaultTop;
        if (top is < 1 or > GetMarginSummaryQuery.MaxTop)
        {
            return Result.Failure<MarginSummaryDto>(new Error("margin_summary.top_invalid", ErrorKind.Validation, $"The number of top products must be between 1 and {GetMarginSummaryQuery.MaxTop}.", "top"));
        }

        var input = await SalesMetricsGatherer.GatherAsync(db, clock.UtcNow, days, cancellationToken);
        var metrics = MarginCalculator.Calculate(input.Lines, input.Products, input.Current, input.Previous);

        return Result.Success(new MarginSummaryDto(
            input.Currency,
            input.TimeZoneId,
            Period(input.Current),
            Period(input.Previous),
            new MarginTotalsDto(MarginFiguresDto.From(metrics.Totals.Current), MarginFiguresDto.From(metrics.Totals.Previous), metrics.Totals.MarginChangePoints),
            metrics.Products.Where(margin => margin.Current.Revenue != 0m || margin.Current.Cost != 0m).Take(top).Select(ProductMarginDto.From).ToList(),
            metrics.Products.Where(margin => margin.Current.GrossMargin < 0m).OrderBy(margin => margin.Current.GrossMargin).ThenBy(margin => margin.Product.Name, StringComparer.Ordinal).ThenBy(margin => margin.Product.ProductId).Select(ProductMarginDto.From).ToList(),
            metrics.Products.Where(MarginCalculator.HasDeclined).OrderBy(margin => margin.ExactMarginChangePoints).ThenBy(margin => margin.Product.Name, StringComparer.Ordinal).ThenBy(margin => margin.Product.ProductId).Select(ProductMarginDto.From).ToList(),
            // Products that sold in the current period with no recorded cost on some or all lines: their margin is not (fully) known.
            metrics.Products.Where(margin => margin.Current.Status is MarginStatus.CostNotRecorded or MarginStatus.PartlyCosted).OrderByDescending(margin => margin.Current.RevenueWithoutCost).ThenBy(margin => margin.Product.Name, StringComparer.Ordinal).ThenBy(margin => margin.Product.ProductId).Select(ProductMarginDto.From).ToList()));
    }

    private static SalesPeriodDto Period(InsightWindow window) => new(window.FirstDay, window.LastDay, window.Days, window.StartUtc, window.EndUtc);
}
