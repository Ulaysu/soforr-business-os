using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Application.Sales.RecordPayment;
using Soforr.Domain.Sales;

namespace Soforr.Application.Sales.ReversePayment;

/// <summary>
/// Takes back one recorded payment in full, as a new record that points at it (the payment itself is never changed). A reason
/// is required. <see cref="ReversalId"/> is an optional, client-chosen identifier of this reversal: sending the same one again
/// changes nothing and reports that it was already recorded, provided it reverses the same payment of the same sale for the
/// same reason; anything different under the same id is a conflict. A payment can be reversed once, and a reversal cannot be
/// reversed.
/// </summary>
public sealed record ReversePaymentCommand(Guid SaleId, Guid PaymentId, Guid? ReversalId, string? Reason);

/// <summary>
/// Records the reversal on the sale's aggregate with its audit event in one transaction; the reversal puts the amount back on
/// what the customer owes. Concurrent changes to the same sale are retried with re-validation, as for a payment. The reason
/// is stored on the reversal and never written to the audit trail.
/// </summary>
public sealed class ReversePaymentHandler(IAppDbContext db, IClock clock, IIdGenerator ids, ICurrentUser user, IAuditLog audit)
    : ICommandHandler<ReversePaymentCommand, PaymentResultDto>
{
    private const int MaxAttempts = 3;

    public async Task<Result<PaymentResultDto>> HandleAsync(ReversePaymentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (user.Require(Permission.PaymentsReverse) is { } denied)
        {
            return Result.Failure<PaymentResultDto>(denied);
        }

        if (command.ReversalId == Guid.Empty)
        {
            return Fail(SalesErrors.Invalid("sale.payment_id_invalid", "reversalId", "The reversal id must be a non-empty identifier or absent."));
        }

        var reversalId = command.ReversalId ?? ids.NewId();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ReverseOnceAsync(command, reversalId, cancellationToken);
            }
            catch (Exception exception) when (IsRetryable(exception) && attempt < MaxAttempts)
            {
                await Task.Delay(RandomNumberGenerator.GetInt32(5, 40), cancellationToken);
            }
            catch (Exception exception) when (IsRetryable(exception))
            {
                db.ClearChangeTracker();
                return Fail(SalesErrors.ConcurrencyConflict);
            }
        }
    }

    private async Task<Result<PaymentResultDto>> ReverseOnceAsync(ReversePaymentCommand command, Guid reversalId, CancellationToken cancellationToken)
    {
        db.ClearChangeTracker();
        var includeCost = user.Has(Permission.ProductViewCost);

        // Filtered to the caller's organization: another organization's sale is not found.
        var sale = await db.Set<Sale>().Include(s => s.Lines).Include(s => s.Payments).SingleOrDefaultAsync(s => s.Id == command.SaleId, cancellationToken);
        if (sale is null)
        {
            return Fail(SalesErrors.SaleNotFound(command.SaleId));
        }

        var existing = await db.Set<SalePayment>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == reversalId, cancellationToken);
        if (existing is not null)
        {
            var same = existing.SaleId == command.SaleId
                && existing.Kind == SalePaymentKind.Reversal
                && existing.ReversesPaymentId == command.PaymentId
                && existing.ReversalReason == command.Reason?.Trim();

            return same
                ? Result.Success(new PaymentResultDto(reversalId, AlreadyRecorded: true, SaleDto.From(sale, includeCost)))
                : Fail(SalesErrors.PaymentIdConflict);
        }

        var reversed = SalesErrors.Run(
            () => sale.ReversePayment(reversalId, command.PaymentId, command.Reason, user.UserId!.Value, clock.UtcNow),
            code => code is "sale.reversal_reason_required" or "sale.reversal_reason_too_long" ? "reason" : code is "sale.timestamp_not_utc" ? "paidAtUtc" : null);
        if (reversed.IsFailure)
        {
            return Fail(reversed.Error!);
        }

        audit.Record(new AuditEntry(sale.OrganizationId, AuditActions.PaymentReversed, user.UserId, "Sale", sale.Id, "A payment was reversed."));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception is not DbUpdateConcurrencyException)
        {
            // A concurrent request may have recorded this reversal id, or reversed the same payment: starting over finds the
            // reversal (a repeat or a conflict) or refuses the second reversal with the business error.
            if (await db.Set<SalePayment>().AnyAsync(p => p.Id == reversalId || p.ReversesPaymentId == command.PaymentId, cancellationToken))
            {
                throw new ReversalRaceException();
            }

            throw;
        }

        return Result.Success(new PaymentResultDto(reversalId, AlreadyRecorded: false, SaleDto.From(sale, includeCost)));
    }

    private static bool IsRetryable(Exception exception) =>
        exception is ReversalRaceException
        || (exception is DbUpdateConcurrencyException concurrency && concurrency.Entries.Any(entry => entry.Entity is Sale or SalePayment));

    private static Result<PaymentResultDto> Fail(Error error) => Result.Failure<PaymentResultDto>(error);

    /// <summary>Thrown inside the handler when a concurrent request recorded the same reversal, so the attempt starts over and finds it.</summary>
    private sealed class ReversalRaceException : Exception;
}
