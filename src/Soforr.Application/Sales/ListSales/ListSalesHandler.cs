using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Sales;

namespace Soforr.Application.Sales.ListSales;

/// <summary>Lists the organization's sales, newest first, optionally of one status (its name, any letter case).</summary>
public sealed record ListSalesQuery(string? Status, int Page, int PageSize)
{
    public const int DefaultPageSize = SalesErrors.DefaultPageSize;
}

public sealed class ListSalesHandler(IAppDbContext db, ICurrentUser user) : IQueryHandler<ListSalesQuery, PagedResult<SaleSummaryDto>>
{
    public async Task<Result<PagedResult<SaleSummaryDto>>> HandleAsync(ListSalesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (user.Require(Permission.SalesView) is { } denied)
        {
            return Result.Failure<PagedResult<SaleSummaryDto>>(denied);
        }

        if (SalesErrors.CheckPaging<SaleSummaryDto>(query.Page, query.PageSize) is { } invalid)
        {
            return invalid;
        }

        SaleStatus? status = null;
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!Enum.TryParse<SaleStatus>(query.Status.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return Result.Failure<PagedResult<SaleSummaryDto>>(SalesErrors.Invalid("sale.status_invalid", "status", $"The status must be one of: {string.Join(", ", Enum.GetNames<SaleStatus>())}."));
            }

            status = parsed;
        }

        var sales = db.Set<Sale>().AsNoTracking().AsQueryable();
        if (status is { } wanted)
        {
            sales = sales.Where(sale => sale.Status == wanted);
        }

        var total = await sales.CountAsync(cancellationToken);
        var skip = (int)((long)(query.Page - 1) * query.PageSize);
        var page = await sales
            .Include(sale => sale.Lines)
            .Include(sale => sale.Payments)
            .OrderByDescending(sale => sale.CreatedAtUtc)
            .ThenByDescending(sale => sale.Id)
            .Skip(skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return Result.Success(new PagedResult<SaleSummaryDto>(page.Select(SaleSummaryDto.From).ToList(), query.Page, query.PageSize, total));
    }
}
