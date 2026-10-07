using Microsoft.EntityFrameworkCore;
using Soforr.Application.Intelligence.Rules;
using Soforr.Application.Persistence;
using Soforr.Domain.Catalog;
using Soforr.Domain.Inventory;
using Soforr.Domain.Organizations;
using Soforr.Domain.Sales;

namespace Soforr.Application.Intelligence.Sources;

/// <summary>
/// Gathers what <see cref="InventoryRiskRule"/> needs: the organization's active products that have reorder settings, their
/// stock, and what each sold in the window (completed sales only, in the organization's time zone), then hands them to the
/// rule. Read-only and tenant-filtered through the database context. The comparisons the database cannot make (a stock
/// quantity is a value object) are made in memory over a set that is small for the target business: the products that have
/// reorder settings and their sale lines of one 30-day window.
/// </summary>
public sealed class InventoryRiskInsightSource(IAppDbContext db) : IInsightSource
{
    public bool RequiresProductViewCost => false;

    public async Task<IReadOnlyList<Insight>> EvaluateAsync(InsightContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Filtered to the caller's organization.
        var timeZoneId = await db.Set<Organization>().AsNoTracking().Select(organization => organization.TimeZoneId).SingleAsync(cancellationToken);
        var window = InsightWindows.TrailingCompleteDays(context.NowUtc, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId), InventoryRiskRule.DefaultWindowDays);

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

        if (rows.Count == 0)
        {
            return [];
        }

        var productIds = rows.Select(row => row.Id).ToArray();
        var start = window.StartUtc;
        var end = window.EndUtc;

        // Only completed sales count: a voided sale is not a sale, and its stock was returned.
        var soldRows = await (
                from line in db.Set<SaleLine>().AsNoTracking()
                join sale in db.Set<Sale>().AsNoTracking() on new { line.OrganizationId, line.SaleId } equals new { sale.OrganizationId, SaleId = sale.Id }
                where sale.Status == SaleStatus.Completed && sale.CreatedAtUtc >= start && sale.CreatedAtUtc < end && productIds.Contains(line.ProductId)
                select new { line.ProductId, line.Quantity })
            .ToListAsync(cancellationToken);

        var sold = soldRows
            .GroupBy(row => row.ProductId)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Quantity.Value));

        var snapshots = rows
            .Select(row => new ProductStockSnapshot(
                row.Id,
                row.Sku,
                row.Name,
                row.UnitOfMeasure,
                IsActive: true,
                row.Quantity?.Value ?? 0m,
                row.ReorderSettings!.ReorderPoint.Value,
                row.ReorderSettings.LeadTimeDays,
                sold.GetValueOrDefault(row.Id)))
            .ToList();

        return InventoryRiskRule.Evaluate(snapshots, window);
    }
}
