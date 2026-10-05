using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Purchasing;

namespace Soforr.Application.Purchasing.ListPurchaseOrders;

/// <summary>Lists the organization's orders, newest first, optionally of one status (its name, any letter case) or one supplier.</summary>
public sealed record ListPurchaseOrdersQuery(string? Status, Guid? SupplierId, int Page, int PageSize)
{
    public const int DefaultPageSize = PurchasingErrors.DefaultPageSize;
}

public sealed class ListPurchaseOrdersHandler(IAppDbContext db, ICurrentUser user) : IQueryHandler<ListPurchaseOrdersQuery, PagedResult<PurchaseOrderSummaryDto>>
{
    public async Task<Result<PagedResult<PurchaseOrderSummaryDto>>> HandleAsync(ListPurchaseOrdersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (user.Require(Permission.PurchasingView) is { } denied)
        {
            return Result.Failure<PagedResult<PurchaseOrderSummaryDto>>(denied);
        }

        if (PurchasingErrors.CheckPaging<PurchaseOrderSummaryDto>(query.Page, query.PageSize) is { } invalid)
        {
            return invalid;
        }

        PurchaseOrderStatus? status = null;
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!Enum.TryParse<PurchaseOrderStatus>(query.Status.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return Result.Failure<PagedResult<PurchaseOrderSummaryDto>>(PurchasingErrors.Invalid("purchase_order.status_invalid", "status", $"The status must be one of: {string.Join(", ", Enum.GetNames<PurchaseOrderStatus>())}."));
            }

            status = parsed;
        }

        var orders = db.Set<PurchaseOrder>().AsNoTracking().AsQueryable();
        if (status is { } wanted)
        {
            orders = orders.Where(order => order.Status == wanted);
        }

        if (query.SupplierId is { } supplierId)
        {
            orders = orders.Where(order => order.SupplierId == supplierId);
        }

        var total = await orders.CountAsync(cancellationToken);
        var skip = (int)((long)(query.Page - 1) * query.PageSize);
        var page = await orders
            .Include(order => order.Lines)
            .OrderByDescending(order => order.CreatedAtUtc)
            .ThenByDescending(order => order.Id)
            .Skip(skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return Result.Success(new PagedResult<PurchaseOrderSummaryDto>(page.Select(PurchaseOrderSummaryDto.From).ToList(), query.Page, query.PageSize, total));
    }
}
