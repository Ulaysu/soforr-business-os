using Microsoft.EntityFrameworkCore;
using Soforr.Application.Catalog.SearchProducts;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Inventory.GetStockLevel;
using Soforr.Application.Inventory.ListLowStock;
using Soforr.Application.Persistence;
using Soforr.Domain.Catalog;
using Soforr.Domain.Inventory;

namespace Soforr.Application.Inventory.ListStockLevels;

/// <summary>One page of the active products with their stock, by name, optionally those matching part of the SKU or name.</summary>
public sealed record ListStockLevelsQuery(string? Text, int Page, int PageSize)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const int MaxPage = 1_000_000;
    public const int MaxTextLength = SearchProductsQuery.MaxTextLength;
}

/// <summary>
/// Lists the stock levels of the organization's active products for a stock screen: the same figures as
/// <c>GetStockLevel</c> (a product that never had stock shows 0, and "low" is the one shared definition), a page at a time, so a screen
/// needs one call instead of one per product. Needs <c>InventoryView</c>. Stock is derived from the movements; nothing is calculated here.
/// </summary>
public sealed class ListStockLevelsHandler(IAppDbContext db, ICurrentUser user) : IQueryHandler<ListStockLevelsQuery, PagedResult<StockLevelDto>>
{
    public async Task<Result<PagedResult<StockLevelDto>>> HandleAsync(ListStockLevelsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (user.Require(Permission.InventoryView) is { } denied)
        {
            return Result.Failure<PagedResult<StockLevelDto>>(denied);
        }

        if (query.Page is < 1 or > ListStockLevelsQuery.MaxPage)
        {
            return Fail("pagination.page_invalid", "page", $"The page must be between 1 and {ListStockLevelsQuery.MaxPage}.");
        }

        if (query.PageSize is < 1 or > ListStockLevelsQuery.MaxPageSize)
        {
            return Fail("pagination.page_size_invalid", "pageSize", $"The page size must be between 1 and {ListStockLevelsQuery.MaxPageSize}.");
        }

        var text = query.Text?.Trim();
        if (text is { Length: > ListStockLevelsQuery.MaxTextLength })
        {
            return Fail("product.search_text_too_long", "q", $"The search text can have at most {ListStockLevelsQuery.MaxTextLength} characters.");
        }

        // Both sets are filtered to the caller's organization.
        var products = db.Set<Product>().AsNoTracking().Where(product => product.IsActive);
        if (!string.IsNullOrEmpty(text))
        {
            products = SearchProductsHandler.MatchingText(products, text);
        }

        var total = await products.CountAsync(cancellationToken);
        var skip = (int)((long)(query.Page - 1) * query.PageSize);
        var page = await products.OrderBy(product => product.Name).ThenBy(product => product.Id).Skip(skip).Take(query.PageSize).ToListAsync(cancellationToken);

        var ids = page.Select(product => product.Id).ToList();
        var levels = await db.Set<StockLevel>().AsNoTracking().Where(level => ids.Contains(level.ProductId)).ToDictionaryAsync(level => level.ProductId, cancellationToken);

        var items = page.Select(product =>
        {
            levels.TryGetValue(product.Id, out var level);
            var quantity = level?.Quantity.Value ?? 0m;

            return new StockLevelDto(
                product.Id,
                product.Sku,
                product.Name,
                product.UnitOfMeasure,
                product.IsActive,
                quantity,
                product.ReorderSettings?.ReorderPoint.Value,
                product.ReorderSettings?.LeadTimeDays,
                LowStock.IsLow(product.IsActive, product.ReorderSettings?.ReorderPoint.Value, quantity),
                level?.UpdatedAtUtc);
        }).ToList();

        return Result.Success(new PagedResult<StockLevelDto>(items, query.Page, query.PageSize, total));
    }

    private static Result<PagedResult<StockLevelDto>> Fail(string code, string field, string message) =>
        Result.Failure<PagedResult<StockLevelDto>>(new Error(code, ErrorKind.Validation, message, field));
}
