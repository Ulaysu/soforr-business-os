using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Catalog;
using Soforr.Domain.Inventory;

namespace Soforr.Application.Inventory.ListLowStock;

/// <summary>
/// The single rule for "low stock", shared by the stock level and the low-stock list so they can never disagree: an
/// <b>active</b> product that has reorder settings is low when its stock is <b>at or below</b> its reorder point (the
/// point at which to reorder). A product that has never had stock counts as 0, so a product with a reorder point of 0
/// is low only when it has none. Inactive products and products without reorder settings are never low.
/// </summary>
public static class LowStock
{
    public static bool IsLow(bool isActive, decimal? reorderPoint, decimal quantity) =>
        isActive && reorderPoint is { } point && quantity <= point;
}

public sealed record ListLowStockQuery(int Page, int PageSize)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const int MaxPage = 1_000_000;
}

/// <summary>A product at or below its reorder point, with the numbers that make it so.</summary>
public sealed record LowStockItemDto(
    Guid ProductId,
    string Sku,
    string Name,
    string UnitOfMeasure,
    decimal Quantity,
    decimal ReorderPoint,
    int ReorderLeadTimeDays);

/// <summary>
/// Lists the products that are low on stock (see <see cref="LowStock"/>), the emptiest first (smallest quantity, then
/// name, then id). The comparison is made in memory over the organization's active products that have reorder
/// settings, because a stock quantity is a value object the database cannot compare; that set is small for the target
/// business (its products with reorder settings), and a database-side comparison can replace it if a customer outgrows it.
/// </summary>
public sealed class ListLowStockHandler(IAppDbContext db, ICurrentUser user) : IQueryHandler<ListLowStockQuery, PagedResult<LowStockItemDto>>
{
    public async Task<Result<PagedResult<LowStockItemDto>>> HandleAsync(ListLowStockQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (user.Require(Permission.InventoryView) is { } denied)
        {
            return Result.Failure<PagedResult<LowStockItemDto>>(denied);
        }

        if (query.Page is < 1 or > ListLowStockQuery.MaxPage)
        {
            return Fail("pagination.page_invalid", "page", $"The page must be between 1 and {ListLowStockQuery.MaxPage}.");
        }

        if (query.PageSize is < 1 or > ListLowStockQuery.MaxPageSize)
        {
            return Fail("pagination.page_size_invalid", "pageSize", $"The page size must be between 1 and {ListLowStockQuery.MaxPageSize}.");
        }

        // Both sets are filtered to the caller's organization.
        var candidates = await (
                from product in db.Set<Product>().AsNoTracking()
                where product.IsActive && product.ReorderSettings != null
                join level in db.Set<StockLevel>().AsNoTracking() on product.Id equals level.ProductId into levels
                from level in levels.DefaultIfEmpty()
                select new
                {
                    product.Id,
                    product.Sku,
                    product.Name,
                    product.UnitOfMeasure,
                    product.ReorderSettings,
                    Quantity = level == null ? null : level.Quantity,
                })
            .ToListAsync(cancellationToken);

        var low = candidates
            .Select(row => new LowStockItemDto(
                row.Id,
                row.Sku,
                row.Name,
                row.UnitOfMeasure,
                row.Quantity?.Value ?? 0m,
                row.ReorderSettings!.ReorderPoint.Value,
                row.ReorderSettings.LeadTimeDays))
            .Where(item => LowStock.IsLow(isActive: true, item.ReorderPoint, item.Quantity))
            .OrderBy(item => item.Quantity)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.ProductId)
            .ToList();

        var skip = (int)((long)(query.Page - 1) * query.PageSize);
        var page = low.Skip(skip).Take(query.PageSize).ToList();

        return Result.Success(new PagedResult<LowStockItemDto>(page, query.Page, query.PageSize, low.Count));
    }

    private static Result<PagedResult<LowStockItemDto>> Fail(string code, string field, string message) =>
        Result.Failure<PagedResult<LowStockItemDto>>(new Error(code, ErrorKind.Validation, message, field));
}
