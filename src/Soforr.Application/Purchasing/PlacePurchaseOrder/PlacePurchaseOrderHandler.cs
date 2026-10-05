using Microsoft.EntityFrameworkCore;
using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Purchasing;

namespace Soforr.Application.Purchasing.PlacePurchaseOrder;

public sealed record PlacePurchaseOrderCommand(Guid Id);

/// <summary>Sends a draft order to the supplier: its lines are fixed from then on. Placing again is a rule violation, not a repeat.</summary>
public sealed class PlacePurchaseOrderHandler(IAppDbContext db, IClock clock, ICurrentUser user, IAuditLog audit)
    : ICommandHandler<PlacePurchaseOrderCommand, PurchaseOrderDto>
{
    public async Task<Result<PurchaseOrderDto>> HandleAsync(PlacePurchaseOrderCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (user.Require(Permission.PurchasingManage) is { } denied)
        {
            return Result.Failure<PurchaseOrderDto>(denied);
        }

        // Filtered to the caller's organization: another organization's order is not found.
        var order = await db.Set<PurchaseOrder>().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == command.Id, cancellationToken);
        if (order is null)
        {
            return Result.Failure<PurchaseOrderDto>(PurchasingErrors.OrderNotFound(command.Id));
        }

        var placed = PurchasingErrors.Run(() =>
        {
            order.Place(clock.UtcNow);
            return order;
        });
        if (placed.IsFailure)
        {
            return Result.Failure<PurchaseOrderDto>(placed.Error!);
        }

        audit.Record(new AuditEntry(order.OrganizationId, AuditActions.PurchaseOrderPlaced, user.UserId, "PurchaseOrder", order.Id, "A purchase order was placed."));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else changed the order between the read and the save; nothing was stored.
            db.ClearChangeTracker();
            return Result.Failure<PurchaseOrderDto>(PurchasingErrors.ConcurrencyConflict);
        }

        return Result.Success(PurchaseOrderDto.From(order));
    }
}
