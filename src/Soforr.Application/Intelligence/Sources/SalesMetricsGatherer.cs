using Microsoft.EntityFrameworkCore;
using Soforr.Application.Intelligence.Rules;
using Soforr.Application.Persistence;
using Soforr.Domain.Catalog;
using Soforr.Domain.Organizations;
using Soforr.Domain.Sales;

namespace Soforr.Application.Intelligence.Sources;

/// <summary>Everything <see cref="SalesMetricsCalculator"/> needs, loaded from the organization's records.</summary>
internal sealed record SalesMetricsInput(
    string Currency,
    string TimeZoneId,
    InsightWindow Current,
    InsightWindow Previous,
    IReadOnlyList<SoldLine> Lines,
    IReadOnlyList<CatalogProduct> Products);

/// <summary>
/// Loads the completed sales of the current and the previous period, and the catalog, for the sales metrics. Read-only and
/// tenant-filtered through the database context. Revenue is the Domain's own line total (<c>SaleLine.LineTotal</c>), so
/// the metrics reconcile exactly with the stored sales. The lines are summed in memory because a quantity is a value object the
/// database cannot sum and the line total is rounded line by line; the set is the sale lines of two periods, which the caller
/// bounds (the summary query limits the period length).
/// </summary>
internal static class SalesMetricsGatherer
{
    public static async Task<SalesMetricsInput> GatherAsync(IAppDbContext db, DateTimeOffset nowUtc, int days, CancellationToken cancellationToken)
    {
        // Filtered to the caller's organization.
        var organization = await db.Set<Organization>().AsNoTracking().Select(o => new { o.TimeZoneId, o.Currency }).SingleAsync(cancellationToken);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(organization.TimeZoneId);
        var current = InsightWindows.TrailingCompleteDays(nowUtc, zone, days);
        var previous = InsightWindows.Preceding(current, zone);
        var start = previous.StartUtc;
        var end = current.EndUtc;

        // Only completed sales count: a voided sale is not a sale, and its stock was returned.
        var sales = await db.Set<Sale>().AsNoTracking()
            .Where(sale => sale.Status == SaleStatus.Completed && sale.CreatedAtUtc >= start && sale.CreatedAtUtc < end)
            .Select(sale => new { sale.Id, sale.CreatedAtUtc })
            .ToListAsync(cancellationToken);
        var soldAt = sales.ToDictionary(sale => sale.Id, sale => sale.CreatedAtUtc);

        var saleLines = await db.Set<SaleLine>().AsNoTracking()
            .Where(line => db.Set<Sale>().Any(sale => sale.Id == line.SaleId && sale.OrganizationId == line.OrganizationId && sale.Status == SaleStatus.Completed && sale.CreatedAtUtc >= start && sale.CreatedAtUtc < end))
            .ToListAsync(cancellationToken);

        // A sale voided between the two reads has no entry above and is left out, as it should be.
        var lines = saleLines
            .Where(line => soldAt.ContainsKey(line.SaleId))
            .Select(line => new SoldLine(line.SaleId, line.ProductId, soldAt[line.SaleId], line.Quantity.Value, line.LineTotal.Amount))
            .ToList();

        var products = await db.Set<Product>().AsNoTracking()
            .Select(product => new CatalogProduct(product.Id, product.Sku, product.Name, product.UnitOfMeasure, product.IsActive, product.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return new SalesMetricsInput(organization.Currency.Code, organization.TimeZoneId, current, previous, lines, products);
    }
}
