using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Inventory;
using Soforr.Application.Persistence;
using Soforr.Domain.Common;
using Soforr.Domain.Inventory;
using Soforr.Domain.Purchasing;

namespace Soforr.Application.Purchasing.ReceivePurchase;

/// <summary>An amount received against one line of the order.</summary>
public sealed record ReceivePurchaseLine(Guid? LineId, decimal? Quantity);

/// <summary>
/// Records goods received against a placed or partially received order. <see cref="ReceiptId"/> is an optional,
/// client-chosen identifier of this receipt: sending the same one again (a retry after a timeout, a double click)
/// changes nothing and reports that it was already recorded, provided it is exactly the same receipt (same order, same
/// lines, same quantities); the same id with anything different is a conflict. Without an id a new receipt is recorded
/// each time.
/// </summary>
public sealed record ReceivePurchaseCommand(Guid OrderId, Guid? ReceiptId, IReadOnlyList<ReceivePurchaseLine>? Lines);

/// <summary>The outcome of a receipt: the order as it now stands. <see cref="AlreadyRecorded"/> is true when this receipt had been recorded before and nothing changed.</summary>
public sealed record ReceiptResultDto(Guid ReceiptId, bool AlreadyRecorded, PurchaseOrderDto Order);

/// <summary>
/// The receiving use case (ADR 0004). In one transaction it updates the order's received quantities and status, writes
/// one <c>PurchaseReceived</c> movement per line (through the stock recorder, which updates the stock levels) and the
/// receipt record and the audit event; they are saved together or not at all, so a receipt that did not commit does not
/// reserve its id. The receipt id is the receipt's key and the movements' source id, so a repeat is recognized by the
/// stored receipt (and refused by the unique keys if two arrive together). A conflict with another writer of the same stock or the same
/// order is retried a bounded number of times, re-reading and re-validating each time.
/// </summary>
public sealed class ReceivePurchaseHandler(IAppDbContext db, StockMovementRecorder recorder, IClock clock, IIdGenerator ids, ICurrentUser user, IAuditLog audit)
    : ICommandHandler<ReceivePurchaseCommand, ReceiptResultDto>
{
    public async Task<Result<ReceiptResultDto>> HandleAsync(ReceivePurchaseCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (user.Require(Permission.PurchasingReceive) is { } denied)
        {
            return Result.Failure<ReceiptResultDto>(denied);
        }

        if (command.ReceiptId == Guid.Empty)
        {
            return Fail(PurchasingErrors.Invalid("purchase_order.receipt_id_invalid", "receiptId", "The receipt id must be a non-empty identifier or absent."));
        }

        if (command.Lines is not { Count: > 0 } lines)
        {
            return Fail(PurchasingErrors.Invalid("purchase_order.receipt_empty", "lines", "A receipt must receive at least one line."));
        }

        if (lines.Count > PurchasingErrors.MaxLines)
        {
            return Fail(PurchasingErrors.Invalid("purchase_order.too_many_lines", "lines", $"A receipt can have at most {PurchasingErrors.MaxLines} lines."));
        }

        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index] is not { LineId: { } lineId } line || lineId == Guid.Empty)
            {
                return Fail(PurchasingErrors.Invalid("purchase_order.receipt_line_required", $"lines[{index}].lineId", "A receipt line must name an order line."));
            }

            if (line.Quantity is null)
            {
                return Fail(PurchasingErrors.Invalid("purchase_order.receipt_quantity_required", $"lines[{index}].quantity", "The received quantity is required."));
            }
        }

        // A receipt without an id is a new receipt each time; with one, a retry is recognized.
        var receiptId = command.ReceiptId ?? ids.NewId();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await recorder.ExecuteWithRetryAsync(() => ReceiveOnceAsync(command.OrderId, receiptId, lines, cancellationToken), cancellationToken);
            }
            catch (Exception exception) when (IsOrderConflict(exception) && attempt < StockMovementRecorder.MaxAttempts)
            {
                await Task.Delay(RandomNumberGenerator.GetInt32(5, 40), cancellationToken);
            }
            catch (Exception exception) when (IsOrderConflict(exception))
            {
                db.ClearChangeTracker();
                return Fail(PurchasingErrors.ConcurrencyConflict);
            }
        }
    }

    private async Task<Result<ReceiptResultDto>> ReceiveOnceAsync(Guid orderId, Guid receiptId, IReadOnlyList<ReceivePurchaseLine> lines, CancellationToken cancellationToken)
    {
        // Filtered to the caller's organization: another organization's order is not found.
        var order = await db.Set<PurchaseOrder>().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null)
        {
            return Fail(PurchasingErrors.OrderNotFound(orderId));
        }

        var receipt = new List<ReceiptLine>(lines.Count);
        for (var index = 0; index < lines.Count; index++)
        {
            var quantity = PurchasingErrors.Run(() => Quantity.Create(lines[index].Quantity!.Value), _ => $"lines[{index}].quantity");
            if (quantity.IsFailure)
            {
                return Fail(quantity.Error!);
            }

            receipt.Add(new ReceiptLine(lines[index].LineId!.Value, quantity.Value));
        }

        // Has this receipt id been used already (the stored receipt is filtered to the caller's organization)? The same
        // receipt again is a repeat and changes nothing; anything different under the same id is a conflict.
        var recorded = await db.Set<PurchaseReceipt>().AsNoTracking().Include(r => r.Lines).SingleOrDefaultAsync(r => r.Id == receiptId, cancellationToken);
        if (recorded is not null)
        {
            return recorded.IsSameAs(order.Id, receipt)
                ? Result.Success(new ReceiptResultDto(receiptId, AlreadyRecorded: true, PurchaseOrderDto.From(order)))
                : Fail(PurchasingErrors.ReceiptIdConflict);
        }

        var now = clock.UtcNow;
        var received = PurchasingErrors.Run(
            () =>
            {
                order.Receive(receipt, now);
                return order;
            },
            code => code switch
            {
                "purchase_order.receipt_line_duplicate" or "purchase_order.line_not_found" => "lines",
                "purchase_order.receipt_quantity_required" or "purchase_order.receipt_exceeds_ordered" => "lines",
                "purchase_order.receipt_empty" => "lines",
                _ => null,
            });
        if (received.IsFailure)
        {
            return Fail(received.Error!);
        }

        // Stage one movement per line. A refused movement leaves nothing saved: this attempt simply returns the failure.
        var opened = new List<Guid>();
        var entries = new List<PurchaseReceiptEntry>(receipt.Count);
        foreach (var line in receipt)
        {
            var orderLine = order.Lines.Single(l => l.Id == line.LineId);
            var staged = await recorder.StageAsync(
                new RecordMovementCommand(
                    orderLine.ProductId,
                    line.Quantity.Value,
                    InventoryMovementReason.PurchaseReceived,
                    InventorySourceType.PurchaseReceipt,
                    receiptId,
                    user.UserId!.Value,
                    $"Received on purchase order {order.Id}"),
                cancellationToken);
            if (staged.IsFailure)
            {
                return Fail(staged.Error!);
            }

            if (staged.Value.AlreadyRecorded)
            {
                // Another request recorded this receipt after the check above: start over, which will find its receipt.
                throw new ReceiptRaceException();
            }

            if (staged.Value.OpenedLevel)
            {
                opened.Add(orderLine.ProductId);
            }

            entries.Add(new PurchaseReceiptEntry(orderLine.Id, orderLine.ProductId, line.Quantity));
        }

        // The record of this receipt, saved with the order, the movements and the audit event: it is what makes the id mean one receipt.
        var stored = PurchasingErrors.Run(() => PurchaseReceipt.Create(receiptId, order.OrganizationId, order.Id, user.UserId!.Value, now, entries));
        if (stored.IsFailure)
        {
            return Fail(stored.Error!);
        }

        db.Set<PurchaseReceipt>().Add(stored.Value);

        audit.Record(new AuditEntry(
            order.OrganizationId,
            AuditActions.PurchaseReceived,
            user.UserId,
            "PurchaseOrder",
            order.Id,
            string.Create(CultureInfo.InvariantCulture, $"Goods were received on {receipt.Count} line(s); the order is now {order.Status}.")));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception is not DbUpdateConcurrencyException)
        {
            // Recognize what the database now holds instead of assuming: a concurrent request may have recorded this
            // receipt id (the receipt key or the movement idempotency index refused ours) or opened a product's first
            // stock level. Starting over finds the stored receipt: the same one is a repeat, a different one a conflict.
            if (await db.Set<PurchaseReceipt>().AnyAsync(r => r.Id == receiptId, cancellationToken))
            {
                throw new ReceiptRaceException();
            }

            foreach (var productId in opened)
            {
                if (await db.Set<StockLevel>().AnyAsync(level => level.ProductId == productId, cancellationToken))
                {
                    throw new StockLevelRaceException(exception);
                }
            }

            throw;
        }

        return Result.Success(new ReceiptResultDto(receiptId, AlreadyRecorded: false, PurchaseOrderDto.From(order)));
    }

    private static bool IsOrderConflict(Exception exception) =>
        exception is ReceiptRaceException
        || (exception is DbUpdateConcurrencyException concurrency && concurrency.Entries.Any(entry => entry.Entity is PurchaseOrder or PurchaseOrderLine));

    private static Result<ReceiptResultDto> Fail(Error error) => Result.Failure<ReceiptResultDto>(error);

    /// <summary>Thrown inside the handler when a concurrent request recorded the same receipt, so the attempt starts over and reports it as already recorded.</summary>
    private sealed class ReceiptRaceException : Exception;
}
