using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Inventory;
using Soforr.Application.Persistence;
using Soforr.Domain.Inventory;
using Soforr.Domain.Sales;

namespace Soforr.Application.Sales.VoidSale;

/// <summary>Voids a completed sale. A reason is required; it is stored on the sale and never written to the audit trail.</summary>
public sealed record VoidSaleCommand(Guid Id, string? Reason);

/// <summary>
/// Cancels a completed sale after the fact. In one transaction it marks the sale voided (reason, user, time), writes one
/// <c>Return</c> movement per line that puts the sold quantity back through the stock recorder, and records the audit
/// event; they are saved together or not at all. The returns are keyed by the sale id with the reason <c>Return</c>, so
/// they can never collide with the sale's own movements, and a sale can be voided only once: a second void (including
/// one racing the first) is refused, so the stock is returned exactly once.
/// </summary>
public sealed class VoidSaleHandler(IAppDbContext db, StockMovementRecorder recorder, IClock clock, ICurrentUser user, IAuditLog audit)
    : ICommandHandler<VoidSaleCommand, SaleDto>
{
    public async Task<Result<SaleDto>> HandleAsync(VoidSaleCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (user.Require(Permission.SalesVoid) is { } denied)
        {
            return Result.Failure<SaleDto>(denied);
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await recorder.ExecuteWithRetryAsync(() => VoidOnceAsync(command, cancellationToken), cancellationToken);
            }
            catch (Exception exception) when (IsSaleConflict(exception) && attempt < StockMovementRecorder.MaxAttempts)
            {
                await Task.Delay(RandomNumberGenerator.GetInt32(5, 40), cancellationToken);
            }
            catch (Exception exception) when (IsSaleConflict(exception))
            {
                db.ClearChangeTracker();
                return Result.Failure<SaleDto>(SalesErrors.ConcurrencyConflict);
            }
        }
    }

    private async Task<Result<SaleDto>> VoidOnceAsync(VoidSaleCommand command, CancellationToken cancellationToken)
    {
        // Filtered to the caller's organization: another organization's sale is not found.
        var sale = await db.Set<Sale>().Include(s => s.Lines).Include(s => s.Payments).SingleOrDefaultAsync(s => s.Id == command.Id, cancellationToken);
        if (sale is null)
        {
            return Result.Failure<SaleDto>(SalesErrors.SaleNotFound(command.Id));
        }

        var voided = SalesErrors.Run(
            () =>
            {
                sale.Void(user.UserId!.Value, command.Reason, clock.UtcNow);
                return sale;
            },
            code => code is "sale.void_reason_required" or "sale.void_reason_too_long" ? "reason" : null);
        if (voided.IsFailure)
        {
            return Result.Failure<SaleDto>(voided.Error!);
        }

        // Put every line's quantity back. A returned movement only adds stock, so none can be refused for lack of stock.
        foreach (var line in sale.Lines)
        {
            var staged = await recorder.StageAsync(
                new RecordMovementCommand(line.ProductId, line.Quantity.Value, InventoryMovementReason.Return, InventorySourceType.Sale, sale.Id, user.UserId!.Value, "Sale voided"),
                cancellationToken);
            if (staged.IsFailure)
            {
                return Result.Failure<SaleDto>(staged.Error!);
            }

            if (staged.Value.AlreadyRecorded)
            {
                // The stock of a sale that is not voided was already returned: this is another void in flight. Start over.
                throw new SaleVoidRaceException();
            }
        }

        audit.Record(new AuditEntry(sale.OrganizationId, AuditActions.SaleVoided, user.UserId, "Sale", sale.Id, "A sale was voided and its stock returned."));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception is not DbUpdateConcurrencyException)
        {
            // The movement idempotency index refused ours: another void of this sale committed first. Starting over finds
            // the sale already voided and refuses with the business error.
            if (await db.Set<Sale>().AsNoTracking().AnyAsync(s => s.Id == command.Id && s.Status == SaleStatus.Voided, cancellationToken))
            {
                throw new SaleVoidRaceException();
            }

            throw;
        }

        return Result.Success(SaleDto.From(sale, user.Has(Permission.ProductViewCost)));
    }

    private static bool IsSaleConflict(Exception exception) =>
        exception is SaleVoidRaceException
        || (exception is DbUpdateConcurrencyException concurrency && concurrency.Entries.Any(entry => entry.Entity is Sale or SaleLine));

    /// <summary>Thrown inside the handler when another void of the same sale got there first, so the attempt starts over and finds the sale voided.</summary>
    private sealed class SaleVoidRaceException : Exception;
}
