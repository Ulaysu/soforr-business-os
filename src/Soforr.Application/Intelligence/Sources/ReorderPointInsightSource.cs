using Microsoft.EntityFrameworkCore;
using Soforr.Application.Intelligence.Rules;
using Soforr.Application.Persistence;
using Soforr.Domain.Catalog;
using Soforr.Domain.Inventory;

namespace Soforr.Application.Intelligence.Sources;

/// <summary>
/// Gathers the organization's active products that have reorder settings, with their stock, and hands them to
/// <see cref="ReorderPointRule"/>. Read-only and tenant-filtered through the database context. The same set is read by the
/// low-stock list; the comparison is made in memory by the rule because a stock quantity is a value object the database
/// cannot compare, over a set that is small for the target business.
/// </summary>
public sealed class ReorderPointInsightSource(IAppDbContext db) : IInsightSource
{
    public bool RequiresProductViewCost => false;

    public async Task<IReadOnlyList<Insight>> EvaluateAsync(InsightContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Both sets are filtered to the caller's organization.
        var rows = await (
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

        var snapshots = rows
            .Select(row => new ProductStockSnapshot(
                row.Id,
                row.Sku,
                row.Name,
                row.UnitOfMeasure,
                IsActive: true,
                row.Quantity?.Value ?? 0m,
                row.ReorderSettings!.ReorderPoint.Value,
                row.ReorderSettings.LeadTimeDays))
            .ToList();

        return ReorderPointRule.Evaluate(snapshots);
    }
}
