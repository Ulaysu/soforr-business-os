using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Persistence;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;

namespace Soforr.Application.Catalog;

/// <summary>What a caller supplies for a product, shared by create and update.</summary>
internal sealed record ProductDetails(
    string? Sku,
    string? Name,
    string? UnitOfMeasure,
    decimal? SellingPrice,
    decimal? CostPrice,
    decimal? ReorderPoint,
    int? ReorderLeadTimeDays)
{
    /// <summary>The prices in the organization's currency and the optional reorder settings, validated by the Domain.</summary>
    public Result<(Money Selling, Money Cost, ReorderSettings? Reorder)> Build(Currency currency)
    {
        if (SellingPrice is not { } sellingAmount)
        {
            return Result.Failure<(Money, Money, ReorderSettings?)>(ProductErrors.Required("product.selling_price_required", "sellingPrice", "The selling price is required."));
        }

        if (CostPrice is not { } costAmount)
        {
            return Result.Failure<(Money, Money, ReorderSettings?)>(ProductErrors.Required("product.cost_price_required", "costPrice", "The cost price is required. Enter 0 if there is none."));
        }

        if (ReorderPoint is null != ReorderLeadTimeDays is null)
        {
            return Result.Failure<(Money, Money, ReorderSettings?)>(
                ProductErrors.Required("product.reorder_settings_incomplete", ReorderPoint is null ? "reorderPoint" : "reorderLeadTimeDays", "Enter both the reorder point and the lead time, or neither."));
        }

        var selling = DomainErrors.Run(() => Money.Create(sellingAmount, currency), _ => "sellingPrice");
        if (selling.IsFailure)
        {
            return Result.Failure<(Money, Money, ReorderSettings?)>(selling.Error!);
        }

        var cost = DomainErrors.Run(() => Money.Create(costAmount, currency), _ => "costPrice");
        if (cost.IsFailure)
        {
            return Result.Failure<(Money, Money, ReorderSettings?)>(cost.Error!);
        }

        if (ReorderPoint is not { } point || ReorderLeadTimeDays is not { } days)
        {
            return Result.Success<(Money, Money, ReorderSettings?)>((selling.Value, cost.Value, null));
        }

        var reorder = DomainErrors.Run(
            () => ReorderSettings.Create(Quantity.Create(point), days),
            code => code.StartsWith("quantity.", StringComparison.Ordinal) ? "reorderPoint" : "reorderLeadTimeDays");

        return reorder.IsFailure
            ? Result.Failure<(Money, Money, ReorderSettings?)>(reorder.Error!)
            : Result.Success<(Money, Money, ReorderSettings?)>((selling.Value, cost.Value, reorder.Value));
    }

    /// <summary>
    /// Whether another product of the organization already uses this SKU, compared without regard to case exactly as
    /// the unique index does (ADR 0006). The index is the real guard; this gives the friendly error.
    /// </summary>
    public static Task<bool> SkuInUseAsync(IAppDbContext db, string sku, Guid? exceptProductId, CancellationToken cancellationToken)
    {
        var key = ProductSkuKey.Of(sku);

        return db.Set<Product>().AnyAsync(
            product => EF.Property<string>(product, ProductSkuKey.PropertyName) == key && product.Id != exceptProductId,
            cancellationToken);
    }
}
