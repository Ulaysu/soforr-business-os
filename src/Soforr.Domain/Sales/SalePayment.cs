using Soforr.Domain.Common;

namespace Soforr.Domain.Sales;

/// <summary>What a payment record is. Both are stored with a positive amount; a reversal subtracts.</summary>
public enum SalePaymentKind
{
    /// <summary>Money received against a sale.</summary>
    Payment = 1,

    /// <summary>Takes back one earlier payment in full (a refund or a correction). It is a new record: the payment it reverses is never changed.</summary>
    Reversal = 2,
}

/// <summary>
/// One money event on a sale: a payment received, or the reversal of one. It is <b>recorded, never processed</b>: Soforr does
/// not move money, it keeps the books of money the business says it received. Records are append-only: once written they are
/// never changed or deleted, and a mistake is corrected by a reversal, which is a new record that points at the payment it
/// reverses. A payment can be reversed once, and only in full. The balance of a sale is derived from these records.
/// </summary>
public sealed class SalePayment
{
    public const int MaxMethodLabelLength = 50;
    public const int MaxReversalReasonLength = 500;

    // Required by the persistence layer for materialization; not part of the domain API.
    private SalePayment()
    {
        Amount = null!;
    }

    internal SalePayment(
        Guid id,
        Guid organizationId,
        Guid saleId,
        SalePaymentKind kind,
        Money amount,
        DateTimeOffset paidAtUtc,
        string? methodLabel,
        Guid? reversesPaymentId,
        string? reversalReason,
        Guid recordedByUserId,
        DateTimeOffset recordedAtUtc)
    {
        Id = id;
        OrganizationId = organizationId;
        SaleId = saleId;
        Kind = kind;

        // Each record owns its amount: a reversal is built from the amount of the payment it reverses, and persistence cannot
        // track one value under two records.
        Amount = Money.Create(amount.Amount, amount.Currency);
        PaidAtUtc = paidAtUtc;
        MethodLabel = methodLabel;
        ReversesPaymentId = reversesPaymentId;
        ReversalReason = reversalReason;
        RecordedByUserId = recordedByUserId;
        RecordedAtUtc = recordedAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid SaleId { get; private set; }

    public SalePaymentKind Kind { get; private set; }

    /// <summary>Always greater than zero, in the sale's currency. For a reversal it equals the amount of the payment it reverses.</summary>
    public Money Amount { get; private set; }

    /// <summary>When the money was received (for a reversal, when it was taken back). Not before the sale and not in the future when recorded.</summary>
    public DateTimeOffset PaidAtUtc { get; private set; }

    /// <summary>A label for how it was paid, for example "Cash" or "Mobile money". Free text, never interpreted.</summary>
    public string? MethodLabel { get; private set; }

    /// <summary>For a reversal, the payment it takes back.</summary>
    public Guid? ReversesPaymentId { get; private set; }

    public string? ReversalReason { get; private set; }

    /// <summary>The user who recorded it.</summary>
    public Guid RecordedByUserId { get; private set; }

    /// <summary>When it was recorded (the system time), which can be later than <see cref="PaidAtUtc"/>.</summary>
    public DateTimeOffset RecordedAtUtc { get; private set; }

    /// <summary>The effect on the amount paid: positive for a payment, negative for a reversal.</summary>
    public decimal SignedAmount => Kind == SalePaymentKind.Payment ? Amount.Amount : -Amount.Amount;
}
