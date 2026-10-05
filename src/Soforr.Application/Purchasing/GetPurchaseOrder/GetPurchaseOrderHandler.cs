using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Purchasing;

namespace Soforr.Application.Purchasing.GetPurchaseOrder;

public sealed record GetPurchaseOrderQuery(Guid Id);

public sealed class GetPurchaseOrderHandler(IAppDbContext db, ICurrentUser user) : IQueryHandler<GetPurchaseOrderQuery, PurchaseOrderDto>
{
    public async Task<Result<PurchaseOrderDto>> HandleAsync(GetPurchaseOrderQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (user.Require(Permission.PurchasingView) is { } denied)
        {
            return Result.Failure<PurchaseOrderDto>(denied);
        }

        // Filtered to the caller's organization: another organization's order is not found.
        var order = await db.Set<PurchaseOrder>().AsNoTracking().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == query.Id, cancellationToken);

        return order is null
            ? Result.Failure<PurchaseOrderDto>(PurchasingErrors.OrderNotFound(query.Id))
            : Result.Success(PurchaseOrderDto.From(order));
    }
}
