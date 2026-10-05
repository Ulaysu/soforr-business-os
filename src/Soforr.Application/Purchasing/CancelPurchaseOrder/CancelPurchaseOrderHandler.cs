using Microsoft.EntityFrameworkCore;
using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Purchasing;

namespace Soforr.Application.Purchasing.CancelPurchaseOrder;

public sealed record CancelPurchaseOrderCommand(Guid Id);

/// <summary>Abandons a draft or placed order that has received nothing. An order that received goods cannot be cancelled: the stock it added is real.</summary>
public sealed class CancelPurchaseOrderHandler(IAppDbContext db, IClock clock, ICurrentUser user, IAuditLog audit)
    : ICommandHandler<CancelPurchaseOrderCommand, PurchaseOrderDto>
{
    public async Task<Result<PurchaseOrderDto>> HandleAsync(CancelPurchaseOrderCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (user.Require(Permission.PurchasingManage) is { } denied)
        {
            return Result.Failure<PurchaseOrderDto>(denied);
        }

        var order = await db.Set<PurchaseOrder>().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == command.Id, cancellationToken);
        if (order is null)
        {
            return Result.Failure<PurchaseOrderDto>(PurchasingErrors.OrderNotFound(command.Id));
        }

        var cancelled = PurchasingErrors.Run(() =>
        {
            order.Cancel(clock.UtcNow);
            return order;
        });
        if (cancelled.IsFailure)
        {
            return Result.Failure<PurchaseOrderDto>(cancelled.Error!);
        }

        audit.Record(new AuditEntry(order.OrganizationId, AuditActions.PurchaseOrderCancelled, user.UserId, "PurchaseOrder", order.Id, "A purchase order was cancelled."));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else (for example a receipt) changed the order between the read and the save; nothing was stored.
            db.ClearChangeTracker();
            return Result.Failure<PurchaseOrderDto>(PurchasingErrors.ConcurrencyConflict);
        }

        return Result.Success(PurchaseOrderDto.From(order));
    }
}
