using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Offline;
using Soforr.Application.Persistence;
using Soforr.Domain.Common;
using Soforr.Domain.Sales;

namespace Soforr.Application.Sales.RecordPayment;

/// <summary>
/// Records a payment received against a sale made on account. <see cref="PaymentId"/> is an optional, client-chosen identifier
/// of this payment: sending the same one again (a retry after a timeout, a double click) changes nothing and reports that it
/// was already recorded, provided it is exactly the same payment (same sale, same amount, same method, and the same date when
/// one is given); the same id with anything different is a conflict. Without an id a new payment is recorded each time.
/// <see cref="PaidAtUtc"/> is when the money was received (default: now); it cannot be before the sale or in the future.
/// The amount can never be more than what is still owed on the sale.
/// <para>
/// A payment taken offline names the <see cref="OfflineGrantId"/> that vouches for it and its <see cref="PaidAtUtc"/> (required then). The server
/// accepts that time only inside a silent gap it observed on that grant (ADR 0008); otherwise it refuses, records nothing and reserves no id.
/// A payment without a grant keeps the existing rules (a date no later than now). The payment permission is checked as always.
/// </para>
/// </summary>
public sealed record RecordPaymentCommand(Guid SaleId, Guid? PaymentId, decimal? Amount, DateTimeOffset? PaidAtUtc, string? MethodLabel, Guid? OfflineGrantId = null);

/// <summary>The outcome of recording a payment or a reversal: the sale as it now stands. <see cref="AlreadyRecorded"/> is true when it had been recorded before and nothing changed.</summary>
public sealed record PaymentResultDto(Guid PaymentId, bool AlreadyRecorded, SaleDto Sale);

/// <summary>
/// Records the payment on the sale's aggregate, which enforces the rules that involve the whole sale (only a completed sale on
/// account, no more than is owed, dates), together with its audit event, in one transaction. Money is recorded, never processed.
/// The payment id is the record's key, so a repeat is recognized by the stored payment (and refused by the unique key if two
/// arrive together). The sale's concurrency token makes two payments recorded at the same moment unable to save together: the
/// loser is retried a bounded number of times, re-reading and re-validating, so a payment that no longer fits is reported as
/// the rule violation it is.
/// </summary>
public sealed class RecordPaymentHandler(IAppDbContext db, IClock clock, IIdGenerator ids, ICurrentUser user, IAuditLog audit, OfflineEventTimeValidator offline)
    : ICommandHandler<RecordPaymentCommand, PaymentResultDto>
{
    private const int MaxAttempts = 3;

    public async Task<Result<PaymentResultDto>> HandleAsync(RecordPaymentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (user.Require(Permission.PaymentsRecord) is { } denied)
        {
            return Result.Failure<PaymentResultDto>(denied);
        }

        if (command.PaymentId == Guid.Empty)
        {
            return Fail(SalesErrors.Invalid("sale.payment_id_invalid", "paymentId", "The payment id must be a non-empty identifier or absent."));
        }

        if (command.OfflineGrantId == Guid.Empty)
        {
            return Fail(SalesErrors.Invalid("offline.grant_invalid", "offlineGrantId", "The offline grant must be a non-empty identifier or absent."));
        }

        if (command.OfflineGrantId is not null && command.PaidAtUtc is null)
        {
            return Fail(SalesErrors.Invalid("offline.event_time_required", "paidAtUtc", "An offline grant needs the time the payment was received."));
        }

        if (command.OfflineGrantId is not null && command.PaidAtUtc!.Value.Offset != TimeSpan.Zero)
        {
            return Fail(SalesErrors.Invalid("sale.timestamp_not_utc", "paidAtUtc", "Timestamps must be in UTC."));
        }

        if (command.Amount is not { } amount)
        {
            return Fail(SalesErrors.Invalid("sale.payment_amount_required", "amount", "The amount is required."));
        }

        // A payment without an id is a new payment each time; with one, a retry is recognized.
        var paymentId = command.PaymentId ?? ids.NewId();

        // The database keeps microseconds. A date with finer ticks is truncated the same way before it is stored and before a
        // repeat is compared, so a genuine retry of the same request is never mistaken for a different payment.
        command = command with { PaidAtUtc = command.PaidAtUtc is { } given ? new DateTimeOffset(given.Ticks - (given.Ticks % 10), given.Offset) : null };

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RecordOnceAsync(command, paymentId, amount, cancellationToken);
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

    private async Task<Result<PaymentResultDto>> RecordOnceAsync(RecordPaymentCommand command, Guid paymentId, decimal amount, CancellationToken cancellationToken)
    {
        // Every attempt starts from what the database holds now.
        db.ClearChangeTracker();
        var includeCost = user.Has(Permission.ProductViewCost);

        // Filtered to the caller's organization: another organization's sale is not found.
        var sale = await db.Set<Sale>().Include(s => s.Lines).Include(s => s.Payments).SingleOrDefaultAsync(s => s.Id == command.SaleId, cancellationToken);
        if (sale is null)
        {
            return Fail(SalesErrors.SaleNotFound(command.SaleId));
        }

        // Has this payment id been used already (the stored payment is filtered to the caller's organization)? The same
        // payment again is a repeat and changes nothing; anything different under the same id is a conflict.
        var existing = await db.Set<SalePayment>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == paymentId, cancellationToken);
        if (existing is not null)
        {
            var label = string.IsNullOrWhiteSpace(command.MethodLabel) ? null : command.MethodLabel.Trim();
            var same = existing.SaleId == command.SaleId
                && existing.Kind == SalePaymentKind.Payment
                && existing.Amount.Amount == amount
                && existing.MethodLabel == label
                && (command.PaidAtUtc is null || existing.PaidAtUtc == command.PaidAtUtc);

            return same
                ? Result.Success(new PaymentResultDto(paymentId, AlreadyRecorded: true, SaleDto.From(sale, includeCost)))
                : Fail(SalesErrors.PaymentIdConflict);
        }

        // A payment taken offline is judged before it is recorded: the server must have vouched for its time (ADR 0008).
        AcceptedOfflineEvent? accepted = null;
        if (command.OfflineGrantId is not null)
        {
            var vouched = await offline.ValidateAsync(command.OfflineGrantId, command.PaidAtUtc!.Value, "payment", cancellationToken);
            if (vouched.IsFailure)
            {
                return Fail(vouched.Error!);
            }

            accepted = vouched.Value;
        }

        var now = clock.UtcNow;
        var recorded = SalesErrors.Run(
            () => sale.RecordPayment(paymentId, Money.Create(amount, sale.Currency), command.PaidAtUtc ?? now, command.MethodLabel, user.UserId!.Value, now),
            PaymentField);
        if (recorded.IsFailure)
        {
            return Fail(recorded.Error!);
        }

        audit.Record(new AuditEntry(
            sale.OrganizationId,
            AuditActions.PaymentRecorded,
            user.UserId,
            "Sale",
            sale.Id,
            string.Create(CultureInfo.InvariantCulture, $"A payment of {recorded.Value.Amount.Amount:0.####} was recorded against a sale.")));

        if (accepted is not null)
        {
            offline.RecordAccepted(accepted, AuditActions.OfflinePaymentAccepted, sale.Id, "Sale", "payment");
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception is not DbUpdateConcurrencyException)
        {
            // Recognize what the database now holds instead of assuming: a concurrent request may have recorded this same
            // payment id. Starting over finds it: the same payment is a repeat, a different one a conflict.
            if (await db.Set<SalePayment>().AnyAsync(p => p.Id == paymentId, cancellationToken))
            {
                throw new PaymentRaceException();
            }

            throw;
        }

        return Result.Success(new PaymentResultDto(paymentId, AlreadyRecorded: false, SaleDto.From(sale, includeCost)));
    }

    private static string? PaymentField(string code) => code switch
    {
        "sale.payment_amount_required" or "sale.payment_currency_mismatch" or "money.scale_exceeded" or "money.amount_out_of_range" => "amount",
        "sale.payment_before_sale" or "sale.payment_in_future" or "sale.timestamp_not_utc" => "paidAtUtc",
        "sale.payment_method_too_long" => "method",
        _ => null,
    };

    private static bool IsRetryable(Exception exception) =>
        exception is PaymentRaceException
        || (exception is DbUpdateConcurrencyException concurrency && concurrency.Entries.Any(entry => entry.Entity is Sale or SalePayment));

    private static Result<PaymentResultDto> Fail(Error error) => Result.Failure<PaymentResultDto>(error);

    /// <summary>Thrown inside the handler when a concurrent request recorded the same payment id, so the attempt starts over and finds it.</summary>
    private sealed class PaymentRaceException : Exception;
}
