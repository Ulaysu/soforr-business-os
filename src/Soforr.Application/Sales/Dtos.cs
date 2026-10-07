using Soforr.Domain.Sales;

namespace Soforr.Application.Sales;

/// <summary>
/// One line of a sale as it is returned to callers. The SKU, name, unit and prices are the ones recorded when the sale
/// happened. <see cref="CostPrice"/> and <see cref="LineCost"/> are null for a caller who may not see costs.
/// </summary>
public sealed record SaleLineDto(
    Guid Id,
    Guid ProductId,
    string Sku,
    string Name,
    string UnitOfMeasure,
    decimal Quantity,
    decimal CatalogUnitPrice,
    decimal UnitPrice,
    string? PriceOverrideReason,
    decimal LineTotal,
    decimal? CostPrice,
    decimal? LineCost)
{
    internal static SaleLineDto From(SaleLine line, bool includeCost)
    {
        ArgumentNullException.ThrowIfNull(line);

        return new SaleLineDto(
            line.Id,
            line.ProductId,
            line.SkuSnapshot,
            line.NameSnapshot,
            line.UnitOfMeasureSnapshot,
            line.Quantity.Value,
            line.CatalogUnitPrice.Amount,
            line.UnitPrice.Amount,
            line.PriceOverrideReason,
            line.LineTotal.Amount,
            includeCost ? line.CostPrice.Amount : null,
            includeCost ? line.LineCost.Amount : null);
    }
}

/// <summary>A sale with its lines. <see cref="Total"/> is computed by the Domain, never supplied. <see cref="TotalCost"/> is null for a caller who may not see costs.</summary>
public sealed record SaleDto(
    Guid Id,
    string Status,
    string Currency,
    decimal Total,
    decimal TotalDiscount,
    decimal? TotalCost,
    IReadOnlyList<SaleLineDto> Lines,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? VoidedAtUtc,
    Guid? VoidedByUserId,
    string? VoidReason)
{
    internal static SaleDto From(Sale sale, bool includeCost)
    {
        ArgumentNullException.ThrowIfNull(sale);

        return new SaleDto(
            sale.Id,
            sale.Status.ToString(),
            sale.Currency.Code,
            sale.Total.Amount,
            sale.TotalDiscount.Amount,
            includeCost ? sale.TotalCost.Amount : null,
            // The database returns lines in no particular order; a stable order (name, SKU, then id) keeps responses repeatable.
            sale.Lines
                .OrderBy(line => line.NameSnapshot, StringComparer.Ordinal)
                .ThenBy(line => line.SkuSnapshot, StringComparer.Ordinal)
                .ThenBy(line => line.Id)
                .Select(line => SaleLineDto.From(line, includeCost))
                .ToList(),
            sale.CreatedByUserId,
            sale.CreatedAtUtc,
            sale.VoidedAtUtc,
            sale.VoidedByUserId,
            sale.VoidReason);
    }
}

/// <summary>A sale without its lines, for lists.</summary>
public sealed record SaleSummaryDto(Guid Id, string Status, string Currency, decimal Total, int LineCount, Guid CreatedByUserId, DateTimeOffset CreatedAtUtc, DateTimeOffset? VoidedAtUtc)
{
    internal static SaleSummaryDto From(Sale sale)
    {
        ArgumentNullException.ThrowIfNull(sale);

        return new SaleSummaryDto(sale.Id, sale.Status.ToString(), sale.Currency.Code, sale.Total.Amount, sale.Lines.Count, sale.CreatedByUserId, sale.CreatedAtUtc, sale.VoidedAtUtc);
    }
}

/// <summary>The outcome of recording a sale: the sale as it now stands. <see cref="AlreadyRecorded"/> is true when this sale had been recorded before and nothing changed.</summary>
public sealed record SaleResultDto(Guid SaleId, bool AlreadyRecorded, SaleDto Sale);
