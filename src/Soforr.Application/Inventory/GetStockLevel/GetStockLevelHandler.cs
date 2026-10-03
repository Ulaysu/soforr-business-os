using Microsoft.EntityFrameworkCore;
using Soforr.Application.Catalog;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Inventory.ListLowStock;
using Soforr.Application.Persistence;
using Soforr.Domain.Catalog;
using Soforr.Domain.Inventory;

namespace Soforr.Application.Inventory.GetStockLevel;

public sealed record GetStockLevelQuery(Guid ProductId);

/// <summary>
/// A product's stock as the caller sees it. <see cref="Quantity"/> is 0 for a product that has never had stock.
/// <see cref="IsLow"/> is true for an active product with reorder settings whose stock is at or below its reorder point
/// (see <c>LowStock</c> in the low-stock list).
/// </summary>
public sealed record StockLevelDto(
    Guid ProductId,
    string Sku,
    string Name,
    string UnitOfMeasure,
    bool IsActive,
    decimal Quantity,
    decimal? ReorderPoint,
    int? ReorderLeadTimeDays,
    bool IsLow,
    DateTimeOffset? UpdatedAtUtc);

public sealed class GetStockLevelHandler(IAppDbContext db, ICurrentUser user) : IQueryHandler<GetStockLevelQuery, StockLevelDto>
{
    public async Task<Result<StockLevelDto>> HandleAsync(GetStockLevelQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (user.Require(Permission.InventoryView) is { } denied)
        {
            return Result.Failure<StockLevelDto>(denied);
        }

        // Both queries are filtered to the caller's organization: another organization's product is not found.
        var product = await db.Set<Product>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == query.ProductId, cancellationToken);
        if (product is null)
        {
            return Result.Failure<StockLevelDto>(ProductErrors.NotFound(query.ProductId));
        }

        var level = await db.Set<StockLevel>().AsNoTracking().SingleOrDefaultAsync(l => l.ProductId == query.ProductId, cancellationToken);
        var quantity = level?.Quantity.Value ?? 0m;

        return Result.Success(new StockLevelDto(
            product.Id,
            product.Sku,
            product.Name,
            product.UnitOfMeasure,
            product.IsActive,
            quantity,
            product.ReorderSettings?.ReorderPoint.Value,
            product.ReorderSettings?.LeadTimeDays,
            LowStock.IsLow(product.IsActive, product.ReorderSettings?.ReorderPoint.Value, quantity),
            level?.UpdatedAtUtc));
    }
}
