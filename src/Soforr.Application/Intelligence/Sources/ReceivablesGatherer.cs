using Microsoft.EntityFrameworkCore;
using Soforr.Application.Intelligence.Rules;
using Soforr.Application.Persistence;
using Soforr.Domain.Customers;
using Soforr.Domain.Organizations;
using Soforr.Domain.Sales;

namespace Soforr.Application.Intelligence.Sources;

/// <summary>Everything the receivables calculation needs, loaded from the organization's records.</summary>
internal sealed record ReceivablesInput(
    string Currency,
    TimeZoneInfo Zone,
    decimal? MaterialityThreshold,
    IReadOnlyList<ReceivableSale> Sales,
    IReadOnlyList<ReceivableCustomer> Customers);

/// <summary>
/// Loads the organization's completed sales on account, with their lines and payments, the customers they belong to, and the
/// organization's time zone and overdue threshold. Read-only and tenant-filtered through the database context. A sale's balance
/// is derived from its lines and payments by the Domain (<c>Sale.Outstanding</c>), so it reconciles exactly with the sale. Sales that owe
/// nothing are dropped here. The sales are read and summed in memory because a sale's total is derived from its lines (rounded line
/// by line); a customer's history on account is the bound.
/// </summary>
internal static class ReceivablesGatherer
{
    public static async Task<ReceivablesInput> GatherAsync(IAppDbContext db, CancellationToken cancellationToken)
    {
        // Filtered to the caller's organization.
        var organization = await db.Set<Organization>().AsNoTracking()
            .Select(o => new { o.TimeZoneId, o.Currency, o.OverdueMaterialityThreshold })
            .SingleAsync(cancellationToken);

        var sales = await db.Set<Sale>().AsNoTracking()
            .Include(sale => sale.Lines)
            .Include(sale => sale.Payments)
            .Where(sale => sale.CustomerId != null && sale.Status == SaleStatus.Completed)
            .ToListAsync(cancellationToken);

        var open = sales
            .Where(sale => sale.Outstanding.Amount > 0m)
            .Select(sale => new ReceivableSale(sale.Id, sale.CustomerId!.Value, sale.CreatedAtUtc, sale.DueAtUtc!.Value, sale.Total.Amount, sale.AmountPaid.Amount))
            .ToList();

        var customerIds = open.Select(sale => sale.CustomerId).Distinct().ToArray();
        var customers = customerIds.Length == 0
            ? []
            : await db.Set<Customer>().AsNoTracking()
                .Where(customer => customerIds.Contains(customer.Id))
                .Select(customer => new ReceivableCustomer(customer.Id, customer.Name))
                .ToListAsync(cancellationToken);

        return new ReceivablesInput(
            organization.Currency.Code,
            TimeZoneInfo.FindSystemTimeZoneById(organization.TimeZoneId),
            organization.OverdueMaterialityThreshold,
            open,
            customers);
    }
}
