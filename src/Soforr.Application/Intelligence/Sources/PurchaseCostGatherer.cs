using Microsoft.EntityFrameworkCore;
using Soforr.Application.Intelligence.Rules;
using Soforr.Application.Persistence;
using Soforr.Domain.Catalog;
using Soforr.Domain.Organizations;
using Soforr.Domain.Purchasing;

namespace Soforr.Application.Intelligence.Sources;

internal sealed record PurchaseCostInput(
    string Currency,
    string TimeZoneId,
    InsightWindow Recent,
    IReadOnlyList<PurchasedLine> Lines,
    IReadOnlyList<PurchaseSupplier> Suppliers,
    IReadOnlyList<CatalogProduct> Products);

/// <summary>
/// Loads the lines of the organization's placed purchase orders (placed, partly received or received; drafts and cancelled orders are not
/// purchases) placed within <see cref="LookbackDays"/>, with their suppliers and the catalog. Read-only and tenant-filtered through the
/// database context. A purchase is dated when its order was placed and costed at the line's unit cost; the line total is the Domain's own
/// (<c>PurchaseOrderLine.LineTotal</c>). The history is bounded by the lookback so the read stays bounded as the business grows; an older
/// purchase is simply not compared.
/// </summary>
internal static class PurchaseCostGatherer
{
    public const int LookbackDays = 365;

    public static async Task<PurchaseCostInput> GatherAsync(IAppDbContext db, DateTimeOffset nowUtc, int recentDays, CancellationToken cancellationToken)
    {
        // Filtered to the caller's organization.
        var organization = await db.Set<Organization>().AsNoTracking().Select(o => new { o.TimeZoneId, o.Currency }).SingleAsync(cancellationToken);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(organization.TimeZoneId);
        var recent = InsightWindows.TrailingCompleteDays(nowUtc, zone, recentDays);
        var since = nowUtc.AddDays(-LookbackDays);

        var orders = await db.Set<PurchaseOrder>().AsNoTracking()
            .Include(order => order.Lines)
            .Where(order => (order.Status == PurchaseOrderStatus.Placed || order.Status == PurchaseOrderStatus.PartiallyReceived || order.Status == PurchaseOrderStatus.Received)
                && order.PlacedAtUtc >= since)
            .ToListAsync(cancellationToken);

        var lines = orders
            .SelectMany(order => order.Lines.Select(line => new PurchasedLine(order.Id, line.ProductId, order.SupplierId, order.PlacedAtUtc!.Value, line.OrderedQuantity.Value, line.UnitCost.Amount, line.LineTotal.Amount)))
            .ToList();

        var supplierIds = orders.Select(order => order.SupplierId).Distinct().ToArray();
        var suppliers = supplierIds.Length == 0
            ? []
            : await db.Set<Supplier>().AsNoTracking()
                .Where(supplier => supplierIds.Contains(supplier.Id))
                .Select(supplier => new PurchaseSupplier(supplier.Id, supplier.Name))
                .ToListAsync(cancellationToken);

        var productIds = lines.Select(line => line.ProductId).Distinct().ToArray();
        var products = productIds.Length == 0
            ? []
            : await db.Set<Product>().AsNoTracking()
                .Where(product => productIds.Contains(product.Id))
                .Select(product => new CatalogProduct(product.Id, product.Sku, product.Name, product.UnitOfMeasure, product.IsActive, product.CreatedAtUtc))
                .ToListAsync(cancellationToken);

        return new PurchaseCostInput(organization.Currency.Code, organization.TimeZoneId, recent, lines, suppliers, products);
    }
}
