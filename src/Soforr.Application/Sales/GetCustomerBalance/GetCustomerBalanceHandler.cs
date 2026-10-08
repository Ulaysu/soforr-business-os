using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Customers;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Customers;
using Soforr.Domain.Sales;

namespace Soforr.Application.Sales.GetCustomerBalance;

public sealed record GetCustomerBalanceQuery(Guid CustomerId);

/// <summary>One sale on account that the customer has not finished paying. <see cref="IsOverdue"/> is true when <see cref="DueAtUtc"/> has passed.</summary>
public sealed record OpenSaleDto(Guid SaleId, DateTimeOffset SoldAtUtc, DateTimeOffset DueAtUtc, decimal Total, decimal AmountPaid, decimal Outstanding, bool IsOverdue);

/// <summary>
/// What a customer owes, as of <see cref="AsOfUtc"/>. <see cref="TotalOutstanding"/> is the sum, over the customer's completed
/// sales on account, of each sale's total minus its payments (net of reversals); <see cref="OverdueAmount"/> is the part of it on
/// sales whose due date has passed. Both are derived from the sales and payment records on every request and are never stored or
/// edited, so they can be reproduced from the records. Only sales that still owe something are listed, the earliest due first.
/// </summary>
public sealed record CustomerBalanceDto(
    Guid CustomerId,
    string CustomerName,
    string Currency,
    DateTimeOffset AsOfUtc,
    decimal TotalOutstanding,
    decimal OverdueAmount,
    int OpenSaleCount,
    int OverdueSaleCount,
    IReadOnlyList<OpenSaleDto> OpenSales);

/// <summary>
/// The customer's balance. A sale is overdue when its due date is strictly before the evaluation time; the day-by-day aging
/// buckets (and the organization's time zone) belong to the aging intelligence (INTEL-005). Voided sales and walk-in sales owe
/// nothing and are not counted; an inactive customer's balance can still be read. The sales are read with their lines and
/// payments and summed in memory, because a sale's total is derived from its lines.
/// </summary>
public sealed class GetCustomerBalanceHandler(IAppDbContext db, IClock clock, ICurrentUser user) : IQueryHandler<GetCustomerBalanceQuery, CustomerBalanceDto>
{
    public async Task<Result<CustomerBalanceDto>> HandleAsync(GetCustomerBalanceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (user.Require(Permission.SalesView) is { } denied)
        {
            return Result.Failure<CustomerBalanceDto>(denied);
        }

        // Filtered to the caller's organization: another organization's customer is not found.
        var customer = await db.Set<Customer>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == query.CustomerId, cancellationToken);
        if (customer is null)
        {
            return Result.Failure<CustomerBalanceDto>(CustomerErrors.NotFound(query.CustomerId));
        }

        var now = clock.UtcNow;
        var sales = await db.Set<Sale>().AsNoTracking()
            .Include(sale => sale.Lines)
            .Include(sale => sale.Payments)
            .Where(sale => sale.CustomerId == query.CustomerId && sale.Status == SaleStatus.Completed)
            .ToListAsync(cancellationToken);

        var open = sales
            .Where(sale => sale.Outstanding.Amount > 0m)
            .Select(sale => new OpenSaleDto(
                sale.Id,
                sale.CreatedAtUtc,
                sale.DueAtUtc!.Value,
                sale.Total.Amount,
                sale.AmountPaid.Amount,
                sale.Outstanding.Amount,
                sale.DueAtUtc.Value < now))
            .OrderBy(item => item.DueAtUtc)
            .ThenBy(item => item.SoldAtUtc)
            .ThenBy(item => item.SaleId)
            .ToList();

        // The currency of the organization's sales; with none yet, the organization's own.
        var currency = sales.FirstOrDefault()?.Currency.Code
            ?? (await db.Set<Soforr.Domain.Organizations.Organization>().AsNoTracking().Select(organization => organization.Currency).SingleAsync(cancellationToken)).Code;

        return Result.Success(new CustomerBalanceDto(
            customer.Id,
            customer.Name,
            currency,
            now,
            open.Sum(item => item.Outstanding),
            open.Where(item => item.IsOverdue).Sum(item => item.Outstanding),
            open.Count,
            open.Count(item => item.IsOverdue),
            open));
    }
}
