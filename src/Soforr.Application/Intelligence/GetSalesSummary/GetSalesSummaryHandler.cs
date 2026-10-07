using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Intelligence.Rules;
using Soforr.Application.Intelligence.Sources;
using Soforr.Application.Persistence;

namespace Soforr.Application.Intelligence.GetSalesSummary;

/// <summary>
/// Sales for the <see cref="Days"/> complete local days before today compared with the same number of days before those
/// (default <see cref="DefaultDays"/>, at most <see cref="MaxDays"/>), with the <see cref="Top"/> best sellers (default
/// <see cref="DefaultTop"/>, at most <see cref="MaxTop"/>).
/// </summary>
public sealed record GetSalesSummaryQuery(int? Days, int? Top)
{
    public const int DefaultDays = 30;

    /// <summary>The longest period: about a quarter. The lines of two periods are summed in memory, so the length is bounded (see <see cref="SalesMetricsGatherer"/>).</summary>
    public const int MaxDays = 92;

    public const int DefaultTop = 10;

    public const int MaxTop = 50;
}

/// <summary>A period: whole local days in the organization's time zone, from the start of <see cref="FirstDay"/> to the start of the day after <see cref="LastDay"/>.</summary>
public sealed record SalesPeriodDto(DateOnly FirstDay, DateOnly LastDay, int Days, DateTimeOffset StartUtc, DateTimeOffset EndUtc);

/// <summary>
/// Revenue and sale count for the two periods. <see cref="RevenueGrowthPercent"/> is <c>(current − previous) ÷ previous × 100</c>
/// to 1 decimal place, or null with <see cref="GrowthStatus"/> <c>no_comparable_previous_sales</c> when previous revenue is 0.
/// </summary>
public sealed record SalesTotalsDto(decimal Revenue, decimal PreviousRevenue, decimal? RevenueGrowthPercent, string GrowthStatus, int SaleCount, int PreviousSaleCount);

/// <summary>What one product sold in the current period and in the period before it. <see cref="QuantityChangePercent"/> is null when it sold nothing in the previous period.</summary>
public sealed record ProductSalesDto(
    Guid ProductId,
    string Sku,
    string Name,
    string UnitOfMeasure,
    decimal Quantity,
    decimal Revenue,
    decimal PreviousQuantity,
    decimal PreviousRevenue,
    decimal? QuantityChangePercent)
{
    internal static ProductSalesDto From(ProductSales sales) => new(
        sales.Product.ProductId,
        sales.Product.Sku,
        sales.Product.Name,
        sales.Product.UnitOfMeasure,
        sales.Quantity,
        sales.Revenue,
        sales.PreviousQuantity,
        sales.PreviousRevenue,
        sales.PreviousQuantity > 0m ? decimal.Round((sales.Quantity - sales.PreviousQuantity) / sales.PreviousQuantity * 100m, 1, MidpointRounding.AwayFromZero) : null);
}

public sealed record DecliningProductDto(ProductSalesDto Product, decimal DeclinePercent);

/// <summary>The slowest sellers. When <see cref="ShowsFullList"/> is true fewer than 5 products qualified and every one of them is listed in order.</summary>
public sealed record SlowMoversDto(int QualifyingProducts, int BottomPercent, bool ShowsFullList, IReadOnlyList<ProductSalesDto> Items);

public sealed record SalesSummaryDto(
    string Currency,
    string TimeZoneId,
    SalesPeriodDto Current,
    SalesPeriodDto Previous,
    SalesTotalsDto Totals,
    IReadOnlyList<ProductSalesDto> TopSellers,
    SlowMoversDto SlowMovers,
    IReadOnlyList<DecliningProductDto> DecliningProducts);

/// <summary>
/// The sales metrics (<see cref="SalesMetricsCalculator"/>) for the caller's organization: revenue and quantity against the
/// immediately preceding period of equal length, top sellers, slow movers and declining products. Computed on request from the
/// completed sales; nothing is stored. Needs <c>InsightsView</c> (the owner's and manager's view of the business, not a cashier's).
/// </summary>
public sealed class GetSalesSummaryHandler(IAppDbContext db, IClock clock, ICurrentUser user) : IQueryHandler<GetSalesSummaryQuery, SalesSummaryDto>
{
    public async Task<Result<SalesSummaryDto>> HandleAsync(GetSalesSummaryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (user.Require(Permission.InsightsView) is { } denied)
        {
            return Result.Failure<SalesSummaryDto>(denied);
        }

        var days = query.Days ?? GetSalesSummaryQuery.DefaultDays;
        if (days is < 1 or > GetSalesSummaryQuery.MaxDays)
        {
            return Result.Failure<SalesSummaryDto>(new Error("sales_summary.days_invalid", ErrorKind.Validation, $"The period must be between 1 and {GetSalesSummaryQuery.MaxDays} days.", "days"));
        }

        var top = query.Top ?? GetSalesSummaryQuery.DefaultTop;
        if (top is < 1 or > GetSalesSummaryQuery.MaxTop)
        {
            return Result.Failure<SalesSummaryDto>(new Error("sales_summary.top_invalid", ErrorKind.Validation, $"The number of top sellers must be between 1 and {GetSalesSummaryQuery.MaxTop}.", "top"));
        }

        var input = await SalesMetricsGatherer.GatherAsync(db, clock.UtcNow, days, cancellationToken);
        var metrics = SalesMetricsCalculator.Calculate(input.Lines, input.Products, input.Current, input.Previous, top);

        var growth = metrics.Totals.RevenueGrowthPercent;

        return Result.Success(new SalesSummaryDto(
            input.Currency,
            input.TimeZoneId,
            Period(input.Current),
            Period(input.Previous),
            new SalesTotalsDto(
                metrics.Totals.Revenue,
                metrics.Totals.PreviousRevenue,
                growth,
                growth is null ? "no_comparable_previous_sales" : "ok",
                metrics.Totals.SaleCount,
                metrics.Totals.PreviousSaleCount),
            metrics.TopSellers.Select(ProductSalesDto.From).ToList(),
            new SlowMoversDto(metrics.SlowMovers.QualifyingProducts, SalesMetricsCalculator.SlowMoverPercent, metrics.SlowMovers.ShowsFullList, metrics.SlowMovers.Items.Select(ProductSalesDto.From).ToList()),
            metrics.DecliningProducts.Select(item => new DecliningProductDto(ProductSalesDto.From(item.Sales), item.DeclinePercent)).ToList()));
    }

    private static SalesPeriodDto Period(InsightWindow window) => new(window.FirstDay, window.LastDay, window.Days, window.StartUtc, window.EndUtc);
}
