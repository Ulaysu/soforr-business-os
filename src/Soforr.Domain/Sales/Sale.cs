using Soforr.Domain.Common;

namespace Soforr.Domain.Sales;

/// <summary>
/// The facts of one line to record on a sale. Everything the line snapshots is passed in (the Application layer reads it
/// from the product at the time of the sale); the Domain never looks anything up. <paramref name="UnitPrice"/> is the
/// price charged; when it differs from <paramref name="CatalogUnitPrice"/> a <paramref name="PriceOverrideReason"/> is required.
/// </summary>
public sealed record SaleLineInput(
    Guid LineId,
    Guid ProductId,
    string? SkuSnapshot,
    string? NameSnapshot,
    string? UnitOfMeasureSnapshot,
    Quantity Quantity,
    Money CatalogUnitPrice,
    Money UnitPrice,
    Money CostPrice,
    string? PriceOverrideReason);

/// <summary>
/// A sale of products to a walk-in customer, in one currency. It is recorded already completed and is immutable: its
/// lines, prices and totals never change; the only change that can follow is <see cref="Void"/>. Totals are computed
/// here from the lines, never supplied by a caller, and every value is validated completely before the sale exists, so a
/// rejected sale leaves nothing. The sale does not record stock: the application writes the matching inventory movements
/// in the same transaction (SALES-002). The user who recorded the sale is the person accountable for any price override
/// on its lines.
/// <para>
/// <b>Credit and payments (CUSTOMER-002).</b> A sale with a <see cref="CustomerId"/> is a sale on account: it can be paid in
/// full, in part or not at all, and the customer owes the rest (<see cref="Outstanding"/>) from its <see cref="DueAtUtc"/>. A
/// walk-in sale (no customer) is always paid in full at the time of the sale and has no balance. Money is recorded as
/// append-only <see cref="SalePayment"/> records (never processed); the amount paid and the balance are derived from them, so
/// they can be reproduced from the records and are never edited. A payment can never take the amount paid above the total (an
/// over-payment is refused), a mistake is corrected by a reversal (a new record), and a sale with money still on it cannot be
/// voided until that money is reversed. <see cref="PaymentRevision"/> changes with every payment event, so the sale's
/// concurrency token stops two payments recorded at the same moment from together paying more than is owed.
/// </para>
/// </summary>
public sealed class Sale
{
    public const int MaxVoidReasonLength = 500;

    public const int MaxMethodLabelLength = SalePayment.MaxMethodLabelLength;

    private readonly List<SaleLine> _lines = [];
    private readonly List<SalePayment> _payments = [];

    // Required by the persistence layer for materialization; not part of the domain API.
    private Sale()
    {
        Currency = null!;
    }

    private Sale(Guid id, Guid organizationId, Currency currency, Guid createdByUserId, DateTimeOffset createdAtUtc)
    {
        Id = id;
        OrganizationId = organizationId;
        Currency = currency;
        CreatedByUserId = createdByUserId;
        CreatedAtUtc = createdAtUtc;
        Status = SaleStatus.Completed;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>The currency of every amount on the sale (the organization's currency). The Domain only stores it: the Application layer obtains the authoritative currency and passes it in.</summary>
    public Currency Currency { get; private set; }

    public SaleStatus Status { get; private set; }

    public IReadOnlyList<SaleLine> Lines => _lines;

    /// <summary>The user who recorded the sale, and who is accountable for any price override on it.</summary>
    public Guid CreatedByUserId { get; private set; }

    /// <summary>When the sale happened.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? VoidedAtUtc { get; private set; }

    public Guid? VoidedByUserId { get; private set; }

    public string? VoidReason { get; private set; }

    /// <summary>The customer the sale was made on account to; null for a walk-in sale, which is always paid in full.</summary>
    public Guid? CustomerId { get; private set; }

    /// <summary>When what the customer owes for this sale becomes overdue: fixed when the sale is recorded (the sale date plus the customer's credit terms then), so changing the terms later does not move an existing debt. Null for a walk-in sale.</summary>
    public DateTimeOffset? DueAtUtc { get; private set; }

    /// <summary>Counts the payment events on this sale. It is bookkeeping for the concurrency token, not part of the sale's facts.</summary>
    public int PaymentRevision { get; private set; }

    /// <summary>The payments and reversals recorded against this sale, in the order they were recorded.</summary>
    public IReadOnlyList<SalePayment> Payments => _payments;

    /// <summary>What has been paid: the payments minus the reversals. Zero for a walk-in sale, which has no payment records.</summary>
    public Money AmountPaid => Money.Create(_payments.Sum(payment => payment.SignedAmount), Currency);

    /// <summary>
    /// What the customer still owes for this sale: the total minus the amount paid, for a completed sale made on account. A
    /// walk-in sale and a voided sale owe nothing.
    /// </summary>
    public Money Outstanding => CustomerId is not null && Status == SaleStatus.Completed
        ? Total.Subtract(AmountPaid)
        : Money.Zero(Currency);

    /// <summary>What was charged: the sum of the line totals.</summary>
    public Money Total => _lines.Aggregate(Money.Zero(Currency), (sum, line) => sum.Add(line.LineTotal));

    /// <summary>What the goods cost: the sum of the line costs (cost price at the time of the sale).</summary>
    public Money TotalCost => _lines.Aggregate(Money.Zero(Currency), (sum, line) => sum.Add(line.LineCost));

    /// <summary>The total amount taken off catalog prices across the lines.</summary>
    public Money TotalDiscount => _lines.Aggregate(Money.Zero(Currency), (sum, line) => sum.Add(line.DiscountTotal));

    /// <summary>Whether any line was sold at a price other than the catalog price.</summary>
    public bool HasPriceOverride => _lines.Any(line => line.IsPriceOverridden);

    /// <summary>
    /// Records a completed sale with its lines. Each product appears at most once. Quantities are greater than zero, prices
    /// and costs are not negative and are in the sale's currency, and a price that differs from the catalog price needs a
    /// reason (and a reason needs a differing price).
    /// </summary>
    /// <exception cref="DomainException">An invariant is violated.</exception>
    public static Sale Complete(
        Guid id,
        Guid organizationId,
        Currency currency,
        Guid createdByUserId,
        DateTimeOffset createdAtUtc,
        IReadOnlyCollection<SaleLineInput> lines,
        Guid? customerId = null,
        DateTimeOffset? dueAtUtc = null,
        Money? initialPayment = null,
        string? paymentMethodLabel = null)
    {
        ArgumentNullException.ThrowIfNull(currency);
        ArgumentNullException.ThrowIfNull(lines);

        if (id == Guid.Empty)
        {
            throw new DomainException("sale.id_required", "A sale must have an identifier.");
        }

        if (organizationId == Guid.Empty)
        {
            throw new DomainException("sale.organization_required", "A sale must belong to an organization.");
        }

        if (createdByUserId == Guid.Empty)
        {
            throw new DomainException("sale.user_required", "A sale must be recorded by a user.");
        }

        var sale = new Sale(id, organizationId, currency, createdByUserId, RequireUtc(createdAtUtc));

        if (lines.Count == 0)
        {
            throw new DomainException("sale.no_lines", "A sale must have at least one line.");
        }

        foreach (var input in lines)
        {
            sale._lines.Add(BuildLine(sale, input));
        }

        if (sale._lines.Select(line => line.ProductId).Distinct().Count() != sale._lines.Count)
        {
            throw new DomainException("sale.line_product_duplicate", "A product can appear only once on a sale.");
        }

        if (sale._lines.Select(line => line.Id).Distinct().Count() != sale._lines.Count)
        {
            throw new DomainException("sale.line_id_duplicate", "Each line must have its own identifier.");
        }

        // The totals must be representable; computing them now refuses a sale whose total would not fit, before it exists.
        _ = sale.Total;
        _ = sale.TotalCost;
        _ = sale.TotalDiscount;

        sale.SettleAtSale(customerId, dueAtUtc, initialPayment, paymentMethodLabel);

        return sale;
    }

    /// <summary>
    /// Records a payment received against this sale. Only a completed sale made on account can be paid, the amount must be
    /// greater than zero, in the sale's currency and no more than what is still owed (an over-payment is refused), and the
    /// payment date cannot be before the sale or after <paramref name="recordedAtUtc"/>. The payment id is chosen by the
    /// caller so a retried request can be recognized.
    /// </summary>
    /// <exception cref="DomainException">The sale cannot take this payment, or a value is invalid.</exception>
    public SalePayment RecordPayment(Guid paymentId, Money amount, DateTimeOffset paidAtUtc, string? methodLabel, Guid recordedByUserId, DateTimeOffset recordedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(amount);

        RequirePayable();

        if (paymentId == Guid.Empty)
        {
            throw new DomainException("sale.payment_id_required", "A payment must have an identifier.");
        }

        if (_payments.Any(payment => payment.Id == paymentId))
        {
            throw new DomainException("sale.payment_id_duplicate", "This payment id is already on the sale.");
        }

        if (recordedByUserId == Guid.Empty)
        {
            throw new DomainException("sale.payment_user_required", "A payment must be recorded by a user.");
        }

        if (amount.Currency != Currency)
        {
            throw new DomainException("sale.payment_currency_mismatch", "A payment must be in the currency of the sale.");
        }

        if (amount.Amount <= 0m)
        {
            throw new DomainException("sale.payment_amount_required", "A payment must be greater than zero.");
        }

        var validRecordedAt = RequireUtc(recordedAtUtc);
        var validPaidAt = RequireUtc(paidAtUtc);
        var label = ValidMethodLabel(methodLabel);

        if (validPaidAt < CreatedAtUtc)
        {
            throw new DomainException("sale.payment_before_sale", "A payment cannot be dated before the sale.");
        }

        if (validPaidAt > validRecordedAt)
        {
            throw new DomainException("sale.payment_in_future", "A payment cannot be dated in the future.");
        }

        if (amount.Amount > Outstanding.Amount)
        {
            throw new DomainException("sale.payment_exceeds_outstanding", "A payment cannot be more than what is still owed on the sale.");
        }

        var payment = new SalePayment(paymentId, OrganizationId, Id, SalePaymentKind.Payment, amount, validPaidAt, label, null, null, recordedByUserId, validRecordedAt);
        _payments.Add(payment);
        PaymentRevision++;
        return payment;
    }

    /// <summary>
    /// Takes back one earlier payment in full, as a new record that points at it; the payment itself is never changed. A
    /// payment can be reversed once, a reversal cannot be reversed, and a reason is required. The reversal puts the amount
    /// back on what the customer owes.
    /// </summary>
    /// <exception cref="DomainException">The payment is not on the sale, is already reversed, or is itself a reversal; or a value is invalid.</exception>
    public SalePayment ReversePayment(Guid reversalId, Guid paymentId, string? reason, Guid recordedByUserId, DateTimeOffset recordedAtUtc)
    {
        if (CustomerId is null || Status != SaleStatus.Completed)
        {
            throw new DomainException("sale.not_payable", "Only a completed sale made on account has payments.");
        }

        var payment = _payments.SingleOrDefault(candidate => candidate.Id == paymentId)
            ?? throw new DomainException("sale.payment_not_found", "The sale has no such payment.");

        if (payment.Kind != SalePaymentKind.Payment)
        {
            throw new DomainException("sale.payment_not_reversible", "A reversal cannot be reversed.");
        }

        if (_payments.Any(candidate => candidate.ReversesPaymentId == paymentId))
        {
            throw new DomainException("sale.payment_already_reversed", "This payment has already been reversed.");
        }

        if (reversalId == Guid.Empty)
        {
            throw new DomainException("sale.payment_id_required", "A reversal must have an identifier.");
        }

        if (_payments.Any(candidate => candidate.Id == reversalId))
        {
            throw new DomainException("sale.payment_id_duplicate", "This payment id is already on the sale.");
        }

        if (recordedByUserId == Guid.Empty)
        {
            throw new DomainException("sale.payment_user_required", "A reversal must be recorded by a user.");
        }

        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainException("sale.reversal_reason_required", "A reason is required to reverse a payment.");
        }

        if (trimmed.Length > SalePayment.MaxReversalReasonLength)
        {
            throw new DomainException("sale.reversal_reason_too_long", $"The reason can have at most {SalePayment.MaxReversalReasonLength} characters.");
        }

        var validRecordedAt = RequireUtc(recordedAtUtc);
        if (validRecordedAt < payment.RecordedAtUtc)
        {
            throw new DomainException("sale.reversal_before_payment", "A payment cannot be reversed before it was recorded.");
        }

        var reversal = new SalePayment(reversalId, OrganizationId, Id, SalePaymentKind.Reversal, payment.Amount, validRecordedAt, payment.MethodLabel, paymentId, trimmed, recordedByUserId, validRecordedAt);
        _payments.Add(reversal);
        PaymentRevision++;
        return reversal;
    }

    /// <summary>
    /// Cancels the sale after the fact: <c>Completed → Voided</c>. A reason and the user who voids it are required. Only a
    /// completed sale can be voided, and a void is final. The caller writes the stock-restoring movements in the same
    /// transaction.
    /// </summary>
    /// <exception cref="DomainException">The sale is already voided, the reason is missing or too long, or the user or time is invalid.</exception>
    public void Void(Guid voidedByUserId, string? reason, DateTimeOffset voidedAtUtc)
    {
        if (Status != SaleStatus.Completed)
        {
            throw new DomainException("sale.not_voidable", "Only a completed sale can be voided.");
        }

        if (_payments.Sum(payment => payment.SignedAmount) != 0m)
        {
            throw new DomainException("sale.has_payments", "A sale with money recorded on it cannot be voided. Reverse its payments first.");
        }

        if (voidedByUserId == Guid.Empty)
        {
            throw new DomainException("sale.void_user_required", "A void must name the user who voided the sale.");
        }

        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainException("sale.void_reason_required", "A reason is required to void a sale.");
        }

        if (trimmed.Length > MaxVoidReasonLength)
        {
            throw new DomainException("sale.void_reason_too_long", $"The reason can have at most {MaxVoidReasonLength} characters.");
        }

        var validVoidedAtUtc = RequireUtc(voidedAtUtc);
        if (validVoidedAtUtc < CreatedAtUtc)
        {
            throw new DomainException("sale.void_before_sale", "A sale cannot be voided before it happened.");
        }

        Status = SaleStatus.Voided;
        VoidedAtUtc = validVoidedAtUtc;
        VoidedByUserId = voidedByUserId;
        VoidReason = trimmed;
    }

    /// <summary>
    /// Whether a request to sell these products in these quantities is exactly this sale again: the same set of products
    /// (no more, no fewer) with the same quantity of each. Only what the caller asked for is compared, not the prices the
    /// server applied, so a later change of a catalog price does not turn a repeat into a different sale. Quantities are
    /// compared by value.
    /// </summary>
    public bool HasSameLinesAs(IReadOnlyCollection<(Guid ProductId, Quantity Quantity)> requested)
    {
        ArgumentNullException.ThrowIfNull(requested);

        return requested.Count == _lines.Count
            && requested.Select(line => line.ProductId).Distinct().Count() == requested.Count
            && requested.All(line => _lines.Any(stored => stored.ProductId == line.ProductId && stored.Quantity.Value == line.Quantity.Value));
    }

    /// <summary>
    /// What a sale of these lines would total: the same arithmetic as <see cref="Total"/> (each unit price times its quantity,
    /// rounded per line, then summed), available before the sale exists so a caller can default the amount paid to "in full"
    /// without repeating the rules.
    /// </summary>
    /// <exception cref="DomainException">A total cannot be represented.</exception>
    public static Money TotalOf(Currency currency, IReadOnlyCollection<SaleLineInput> lines)
    {
        ArgumentNullException.ThrowIfNull(currency);
        ArgumentNullException.ThrowIfNull(lines);

        return lines.Aggregate(Money.Zero(currency), (sum, line) => sum.Add(line.UnitPrice.Multiply(line.Quantity)));
    }

    /// <summary>Applies the customer, the due date and the money taken with the sale. A walk-in sale is paid in full and carries none of them.</summary>
    private void SettleAtSale(Guid? customerId, DateTimeOffset? dueAtUtc, Money? initialPayment, string? paymentMethodLabel)
    {
        if (customerId == Guid.Empty)
        {
            throw new DomainException("sale.customer_invalid", "The customer must be a non-empty identifier or absent.");
        }

        if (customerId is null)
        {
            if (dueAtUtc is not null || initialPayment is not null)
            {
                throw new DomainException("sale.walk_in_is_paid_in_full", "A walk-in sale is paid in full at the time of sale: it has no due date and no payment to record.");
            }

            return;
        }

        if (dueAtUtc is not { } due)
        {
            throw new DomainException("sale.due_date_required", "A sale made on account must have a due date.");
        }

        if (RequireUtc(due) < CreatedAtUtc)
        {
            throw new DomainException("sale.due_before_sale", "The due date cannot be before the sale.");
        }

        CustomerId = customerId;
        DueAtUtc = due;

        if (initialPayment is null)
        {
            return;
        }

        if (initialPayment.Currency != Currency)
        {
            throw new DomainException("sale.payment_currency_mismatch", "A payment must be in the currency of the sale.");
        }

        if (initialPayment.Amount < 0m)
        {
            throw new DomainException("sale.payment_amount_negative", "The amount paid cannot be negative.");
        }

        if (initialPayment.Amount > Total.Amount)
        {
            throw new DomainException("sale.payment_exceeds_total", "The amount paid cannot be more than the total of the sale.");
        }

        var label = ValidMethodLabel(paymentMethodLabel);
        if (initialPayment.Amount > 0m)
        {
            // The payment taken with the sale has the sale's own id, so it is recognizable and cannot collide with a later one.
            _payments.Add(new SalePayment(Id, OrganizationId, Id, SalePaymentKind.Payment, initialPayment, CreatedAtUtc, label, null, null, CreatedByUserId, CreatedAtUtc));
        }
    }

    /// <summary>
    /// Whether a request to settle a sale this way is exactly how this sale was settled when it was recorded: the same
    /// customer (or none) and the same amount paid at the time of the sale. An absent amount means paid in full. This compares
    /// the original payment, not the balance now, so payments recorded since do not turn a repeat into a different sale.
    /// </summary>
    public bool HasSameSettlementAs(Guid? customerId, decimal? amountPaid)
    {
        if (customerId != CustomerId)
        {
            return false;
        }

        if (CustomerId is null)
        {
            // A walk-in sale is always paid in full: a stated amount must be exactly the total.
            return amountPaid is null || amountPaid == Total.Amount;
        }

        var paidAtSale = _payments.SingleOrDefault(payment => payment.Id == Id && payment.Kind == SalePaymentKind.Payment)?.Amount.Amount ?? 0m;

        return paidAtSale == (amountPaid ?? Total.Amount);
    }

    private void RequirePayable()
    {
        if (CustomerId is null)
        {
            throw new DomainException("sale.not_payable", "Only a sale made on account can be paid: a walk-in sale is paid in full when it is made.");
        }

        if (Status != SaleStatus.Completed)
        {
            throw new DomainException("sale.not_payable", "A voided sale cannot be paid.");
        }
    }

    private static string? ValidMethodLabel(string? methodLabel)
    {
        var trimmed = string.IsNullOrWhiteSpace(methodLabel) ? null : methodLabel.Trim();

        if (trimmed is { Length: > SalePayment.MaxMethodLabelLength })
        {
            throw new DomainException("sale.payment_method_too_long", $"The payment method can have at most {SalePayment.MaxMethodLabelLength} characters.");
        }

        return trimmed;
    }

    private static SaleLine BuildLine(Sale sale, SaleLineInput? input)
    {
        if (input is null)
        {
            throw new DomainException("sale.line_required", "A line is missing.");
        }

        ArgumentNullException.ThrowIfNull(input.Quantity);
        ArgumentNullException.ThrowIfNull(input.CatalogUnitPrice);
        ArgumentNullException.ThrowIfNull(input.UnitPrice);
        ArgumentNullException.ThrowIfNull(input.CostPrice);

        if (input.LineId == Guid.Empty)
        {
            throw new DomainException("sale.line_id_required", "A line must have an identifier.");
        }

        if (input.ProductId == Guid.Empty)
        {
            throw new DomainException("sale.line_product_required", "A line must be for a product.");
        }

        var sku = Snapshot(input.SkuSnapshot, SaleLine.MaxSkuLength, "sku");
        var name = Snapshot(input.NameSnapshot, SaleLine.MaxNameLength, "name");
        var unit = Snapshot(input.UnitOfMeasureSnapshot, SaleLine.MaxUnitOfMeasureLength, "unit_of_measure");

        if (input.Quantity.Value == 0m)
        {
            throw new DomainException("sale.line_quantity_required", "The quantity sold must be greater than zero.");
        }

        if (input.CatalogUnitPrice.Currency != sale.Currency || input.UnitPrice.Currency != sale.Currency || input.CostPrice.Currency != sale.Currency)
        {
            throw new DomainException("sale.line_currency_mismatch", "Every price and cost must be in the currency of the sale.");
        }

        if (input.UnitPrice.Amount < 0m)
        {
            throw new DomainException("sale.line_unit_price_negative", "The price charged cannot be negative.");
        }

        if (input.CatalogUnitPrice.Amount < 0m)
        {
            throw new DomainException("sale.line_catalog_price_negative", "The catalog price cannot be negative.");
        }

        if (input.CostPrice.Amount < 0m)
        {
            throw new DomainException("sale.line_cost_price_negative", "The cost price cannot be negative.");
        }

        var overridden = input.UnitPrice.Amount != input.CatalogUnitPrice.Amount;
        var reason = string.IsNullOrWhiteSpace(input.PriceOverrideReason) ? null : input.PriceOverrideReason.Trim();

        if (overridden && reason is null)
        {
            throw new DomainException("sale.line_override_reason_required", "A price that differs from the catalog price needs a reason.");
        }

        if (!overridden && reason is not null)
        {
            throw new DomainException("sale.line_override_reason_unexpected", "A reason can be given only for a price that differs from the catalog price.");
        }

        if (reason is { Length: > SaleLine.MaxOverrideReasonLength })
        {
            throw new DomainException("sale.line_override_reason_too_long", $"The price reason can have at most {SaleLine.MaxOverrideReasonLength} characters.");
        }

        return new SaleLine(input.LineId, sale.OrganizationId, sale.Id, input.ProductId, sku, name, unit, input.Quantity, input.CatalogUnitPrice, input.UnitPrice, input.CostPrice, reason);
    }

    private static string Snapshot(string? value, int maxLength, string name)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainException($"sale.line_{name}_required", $"The product's {name.Replace('_', ' ')} is required.");
        }

        if (trimmed.Length > maxLength)
        {
            throw new DomainException($"sale.line_{name}_too_long", $"The product's {name.Replace('_', ' ')} can have at most {maxLength} characters.");
        }

        return trimmed;
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new DomainException("sale.timestamp_not_utc", "Timestamps must be in UTC.");
        }

        return value;
    }
}
