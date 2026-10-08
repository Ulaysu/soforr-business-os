using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Intelligence.GetSalesSummary;
using Soforr.Application.Intelligence.Rules;
using Soforr.Application.Intelligence.Sources;
using Soforr.Application.Persistence;

namespace Soforr.Application.Intelligence.GetPurchaseCosts;

/// <summary>Purchase costs: the volume of the last <see cref="Days"/> complete local days (default 30, at most 92) and the <see cref="Top"/> products by spend (default 10, at most 50).</summary>
public sealed record GetPurchaseCostsQuery(int? Days, int? Top)
{
    public const int DefaultDays = GetSalesSummaryQuery.DefaultDays;

    public const int MaxDays = GetSalesSummaryQuery.MaxDays;

    public const int DefaultTop = 10;

    public const int MaxTop = 50;
}

public sealed record PurchasePointDto(Guid OrderId, DateTimeOffset PlacedAtUtc, decimal UnitCost);

/// <summary>A supplier's latest purchase of a product against the one before it. <see cref="Status"/> is <c>ok</c> or <c>insufficient_history</c> (a single costed purchase: no change is computed).</summary>
public sealed record SupplierCostDto(Guid SupplierId, string SupplierName, PurchasePointDto Latest, PurchasePointDto? Previous, decimal? ChangePercent, bool Notable, string Status)
{
    internal static SupplierCostDto From(SupplierCost cost) => new(
        cost.SupplierId,
        cost.SupplierName,
        Point(cost.Latest)!,
        Point(cost.Previous),
        cost.ChangePercent,
        cost.Notable,
        cost.Previous is null ? "insufficient_history" : "ok");

    private static PurchasePointDto? Point(PurchasePoint? point) => point is null ? null : new PurchasePointDto(point.OrderId, point.PlacedAtUtc, point.UnitCost);
}

public sealed record SupplierGapDto(SupplierCostDto Cheapest, SupplierCostDto MostExpensive, decimal GapPercent, bool Notable);

public sealed record PurchaseVolumeDto(int Orders, decimal Quantity, decimal Spend);

public sealed record ProductPurchaseCostDto(
    Guid ProductId,
    string Sku,
    string Name,
    string UnitOfMeasure,
    IReadOnlyList<SupplierCostDto> Suppliers,
    SupplierGapDto? SupplierGap,
    PurchaseVolumeDto Volume,
    DateTimeOffset? LastPurchasedAtUtc,
    int PurchasesWithoutCost)
{
    internal static ProductPurchaseCostDto From(ProductPurchaseCost cost) => new(
        cost.Product.ProductId,
        cost.Product.Sku,
        cost.Product.Name,
        cost.Product.UnitOfMeasure,
        cost.Suppliers.Select(SupplierCostDto.From).ToList(),
        cost.Gap is null ? null : new SupplierGapDto(SupplierCostDto.From(cost.Gap.Cheapest), SupplierCostDto.From(cost.Gap.MostExpensive), cost.Gap.GapPercent, cost.Gap.Notable),
        new PurchaseVolumeDto(cost.Volume.Orders, cost.Volume.Quantity, cost.Volume.Spend),
        cost.LastPurchasedAtUtc,
        cost.PurchasesWithoutCost);
}

/// <summary>
/// <see cref="Period"/> is the window of the <see cref="Volume"/> figures. <see cref="NotableChangeThresholdPercent"/> is the documented 10%.
/// <see cref="TopProducts"/> are by spend in the period; <see cref="NotableChanges"/> are the products with a notable supplier cost change
/// (within the lookback), whatever their spend.
/// </summary>
public sealed record PurchaseCostsDto(
    string Currency,
    string TimeZoneId,
    SalesPeriodDto Period,
    int LookbackDays,
    decimal NotableChangeThresholdPercent,
    PurchaseVolumeDto Volume,
    IReadOnlyList<ProductPurchaseCostDto> TopProducts,
    IReadOnlyList<ProductPurchaseCostDto> NotableChanges);

/// <summary>
/// The purchase cost report (<see cref="PurchaseCostCalculator"/>), computed on request from placed purchase orders; nothing is stored.
/// It reveals costs, so it needs <c>InsightsView</c> and <c>ProductViewCost</c>.
/// </summary>
public sealed class GetPurchaseCostsHandler(IAppDbContext db, IClock clock, ICurrentUser user) : IQueryHandler<GetPurchaseCostsQuery, PurchaseCostsDto>
{
    public async Task<Result<PurchaseCostsDto>> HandleAsync(GetPurchaseCostsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (user.Require(Permission.InsightsView) is { } denied)
        {
            return Result.Failure<PurchaseCostsDto>(denied);
        }

        if (user.Require(Permission.ProductViewCost) is { } deniedCost)
        {
            return Result.Failure<PurchaseCostsDto>(deniedCost);
        }

        var days = query.Days ?? GetPurchaseCostsQuery.DefaultDays;
        if (days is < 1 or > GetPurchaseCostsQuery.MaxDays)
        {
            return Result.Failure<PurchaseCostsDto>(new Error("purchase_costs.days_invalid", ErrorKind.Validation, $"The period must be between 1 and {GetPurchaseCostsQuery.MaxDays} days.", "days"));
        }

        var top = query.Top ?? GetPurchaseCostsQuery.DefaultTop;
        if (top is < 1 or > GetPurchaseCostsQuery.MaxTop)
        {
            return Result.Failure<PurchaseCostsDto>(new Error("purchase_costs.top_invalid", ErrorKind.Validation, $"The number of top products must be between 1 and {GetPurchaseCostsQuery.MaxTop}.", "top"));
        }

        var input = await PurchaseCostGatherer.GatherAsync(db, clock.UtcNow, days, cancellationToken);
        var costs = PurchaseCostCalculator.Calculate(input.Lines, input.Suppliers, input.Products, input.Recent);
        var window = input.Recent;

        return Result.Success(new PurchaseCostsDto(
            input.Currency,
            input.TimeZoneId,
            new SalesPeriodDto(window.FirstDay, window.LastDay, window.Days, window.StartUtc, window.EndUtc),
            PurchaseCostGatherer.LookbackDays,
            PurchaseCostCalculator.NotableChangeFraction * 100m,
            new PurchaseVolumeDto(
                costs.Products.Sum(product => product.Volume.Orders),
                costs.Products.Sum(product => product.Volume.Quantity),
                costs.Products.Sum(product => product.Volume.Spend)),
            costs.Products.Where(product => product.Volume.Orders > 0).Take(top).Select(ProductPurchaseCostDto.From).ToList(),
            costs.Products
                .Where(product => product.Suppliers.Any(supplier => supplier.Notable))
                .OrderBy(product => product.Product.Name, StringComparer.Ordinal)
                .ThenBy(product => product.Product.ProductId)
                .Select(ProductPurchaseCostDto.From)
                .ToList()));
    }
}
