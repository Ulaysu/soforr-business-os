using Soforr.Domain.Catalog;

namespace Soforr.Application.Catalog;

/// <summary>
/// A product as it is returned to callers; the Domain entity is never exposed. Prices are amounts in the
/// organization's <see cref="Currency"/>. <see cref="CostPrice"/> is null for a caller who may not see costs.
/// </summary>
public sealed record ProductDto(
    Guid Id,
    string Sku,
    string Name,
    string UnitOfMeasure,
    string Currency,
    decimal SellingPrice,
    decimal? CostPrice,
    decimal? ReorderPoint,
    int? ReorderLeadTimeDays,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    internal static ProductDto From(Product product, bool includeCost)
    {
        ArgumentNullException.ThrowIfNull(product);

        return new ProductDto(
            product.Id,
            product.Sku,
            product.Name,
            product.UnitOfMeasure,
            product.SellingPrice.Currency.Code,
            product.SellingPrice.Amount,
            includeCost ? product.CostPrice.Amount : null,
            product.ReorderSettings?.ReorderPoint.Value,
            product.ReorderSettings?.LeadTimeDays,
            product.IsActive,
            product.CreatedAtUtc,
            product.UpdatedAtUtc);
    }
}
