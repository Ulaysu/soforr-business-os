using Soforr.Domain.Common;

namespace Soforr.Domain.Purchasing;

/// <summary>An amount received against one line of an order.</summary>
public sealed record ReceiptLine(Guid LineId, Quantity Quantity);

/// <summary>
/// An order to a supplier for products, in one currency. It owns its lines and the rules that involve the whole
/// order, and it is a state machine (see <see cref="PurchaseOrderStatus"/>): lines can be edited only while it is a
/// draft; once placed they are fixed; goods are received against the lines until everything ordered has arrived; an
/// order that has received nothing can be cancelled. Every change is validated completely before any state changes, so
/// a rejected change leaves the order untouched. Totals are computed here, never supplied by a caller.
/// The order does not record stock: receiving updates the received quantities and the status, and the application
/// records the matching inventory movements in the same transaction (PURCHASING-002).
/// </summary>
public sealed class PurchaseOrder
{
    public const int MaxReferenceLength = 100;

    private readonly List<PurchaseOrderLine> _lines = [];

    // Required by the persistence layer for materialization; not part of the domain API.
    private PurchaseOrder()
    {
        Currency = null!;
    }

    private PurchaseOrder(Guid id, Guid organizationId, Guid supplierId, Currency currency, string? reference, DateTimeOffset createdAtUtc)
    {
        Id = id;
        OrganizationId = organizationId;
        SupplierId = supplierId;
        Currency = currency;
        Reference = reference;
        Status = PurchaseOrderStatus.Draft;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid SupplierId { get; private set; }

    /// <summary>The currency of every amount on the order (the organization's currency; the handler supplies it).</summary>
    public Currency Currency { get; private set; }

    /// <summary>An optional reference the user chooses, for example the supplier's quotation number.</summary>
    public string? Reference { get; private set; }

    public PurchaseOrderStatus Status { get; private set; }

    public IReadOnlyList<PurchaseOrderLine> Lines => _lines;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>When the order was placed; null while it is a draft or if it was cancelled as a draft.</summary>
    public DateTimeOffset? PlacedAtUtc { get; private set; }

    /// <summary>When the order was received in full or cancelled; null while it is open.</summary>
    public DateTimeOffset? ClosedAtUtc { get; private set; }

    /// <summary>The cost of everything ordered: the sum of the line totals (rounding policy: <see cref="Money.Multiply"/>).</summary>
    public Money Total => _lines.Aggregate(Money.Zero(Currency), (sum, line) => sum.Add(line.LineTotal));

    /// <exception cref="DomainException">An invariant is violated.</exception>
    public static PurchaseOrder CreateDraft(Guid id, Guid organizationId, Guid supplierId, Currency currency, string? reference, DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(currency);

        if (id == Guid.Empty)
        {
            throw new DomainException("purchase_order.id_required", "A purchase order must have an identifier.");
        }

        if (organizationId == Guid.Empty)
        {
            throw new DomainException("purchase_order.organization_required", "A purchase order must belong to an organization.");
        }

        if (supplierId == Guid.Empty)
        {
            throw new DomainException("purchase_order.supplier_required", "A purchase order must be for a supplier.");
        }

        return new PurchaseOrder(id, organizationId, supplierId, currency, ValidReference(reference), RequireUtc(createdAtUtc));
    }

    /// <summary>Changes the reference. Draft only.</summary>
    /// <exception cref="DomainException">The order is not a draft, or the reference is too long.</exception>
    public void SetReference(string? reference, DateTimeOffset updatedAtUtc)
    {
        RequireDraft();
        var valid = ValidReference(reference);
        var validUpdatedAtUtc = RequireUtc(updatedAtUtc);

        Reference = valid;
        UpdatedAtUtc = validUpdatedAtUtc;
    }

    /// <summary>
    /// Adds a product to the order. Draft only. A product appears at most once on an order (change its line instead), and
    /// the unit cost must be in the order's currency.
    /// </summary>
    /// <exception cref="DomainException">The order is not a draft, the product is already on it, or a value is invalid.</exception>
    public PurchaseOrderLine AddLine(
        Guid lineId,
        Guid productId,
        string? skuSnapshot,
        string? nameSnapshot,
        string? unitOfMeasureSnapshot,
        Quantity orderedQuantity,
        Money unitCost,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(orderedQuantity);
        ArgumentNullException.ThrowIfNull(unitCost);

        RequireDraft();

        if (lineId == Guid.Empty)
        {
            throw new DomainException("purchase_order.line_id_required", "A line must have an identifier.");
        }

        if (productId == Guid.Empty)
        {
            throw new DomainException("purchase_order.line_product_required", "A line must be for a product.");
        }

        if (_lines.Any(line => line.ProductId == productId))
        {
            throw new DomainException("purchase_order.line_product_duplicate", "This product is already on the order. Change its line instead.");
        }

        var sku = Snapshot(skuSnapshot, PurchaseOrderLine.MaxSkuLength, "sku");
        var name = Snapshot(nameSnapshot, PurchaseOrderLine.MaxNameLength, "name");
        var unit = Snapshot(unitOfMeasureSnapshot, PurchaseOrderLine.MaxUnitOfMeasureLength, "unit_of_measure");
        ValidLineValues(orderedQuantity, unitCost);
        var validUpdatedAtUtc = RequireUtc(updatedAtUtc);

        var line = new PurchaseOrderLine(lineId, OrganizationId, Id, productId, sku, name, unit, orderedQuantity, unitCost);
        EnsureTotalFits(_lines.Append(line));

        _lines.Add(line);
        UpdatedAtUtc = validUpdatedAtUtc;
        return line;
    }

    /// <summary>Changes the ordered quantity and unit cost of a line. Draft only.</summary>
    /// <exception cref="DomainException">The order is not a draft, the line does not exist, or a value is invalid.</exception>
    public void UpdateLine(Guid lineId, Quantity orderedQuantity, Money unitCost, DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(orderedQuantity);
        ArgumentNullException.ThrowIfNull(unitCost);

        RequireDraft();
        var line = FindLine(lineId);
        ValidLineValues(orderedQuantity, unitCost);
        var validUpdatedAtUtc = RequireUtc(updatedAtUtc);

        // The total with the new values must still be representable, before anything changes.
        var previous = (line.OrderedQuantity, line.UnitCost);
        line.Change(orderedQuantity, unitCost);
        try
        {
            EnsureTotalFits(_lines);
        }
        catch (DomainException)
        {
            line.Change(previous.OrderedQuantity, previous.UnitCost);
            throw;
        }

        UpdatedAtUtc = validUpdatedAtUtc;
    }

    /// <summary>Removes a line. Draft only.</summary>
    /// <exception cref="DomainException">The order is not a draft or the line does not exist.</exception>
    public void RemoveLine(Guid lineId, DateTimeOffset updatedAtUtc)
    {
        RequireDraft();
        var line = FindLine(lineId);
        var validUpdatedAtUtc = RequireUtc(updatedAtUtc);

        _lines.Remove(line);
        UpdatedAtUtc = validUpdatedAtUtc;
    }

    /// <summary>Sends the order to the supplier: <c>Draft → Placed</c>. The order must have at least one line; the lines are fixed from now on.</summary>
    /// <exception cref="DomainException">The order is not a draft or has no lines.</exception>
    public void Place(DateTimeOffset placedAtUtc)
    {
        if (Status != PurchaseOrderStatus.Draft)
        {
            throw new DomainException("purchase_order.cannot_place", "Only a draft order can be placed.");
        }

        if (_lines.Count == 0)
        {
            throw new DomainException("purchase_order.no_lines", "An order must have at least one line before it can be placed.");
        }

        var validPlacedAtUtc = RequireUtc(placedAtUtc);

        Status = PurchaseOrderStatus.Placed;
        PlacedAtUtc = validPlacedAtUtc;
        UpdatedAtUtc = validPlacedAtUtc;
    }

    /// <summary>
    /// Records goods received against lines: <c>Placed</c> or <c>PartiallyReceived</c> → <c>PartiallyReceived</c>, or
    /// <c>Received</c> once every line has been received in full. Each amount must be greater than zero and cannot take a
    /// line beyond what was ordered; a line appears at most once in one receipt. Nothing changes unless the whole receipt
    /// is valid.
    /// </summary>
    /// <exception cref="DomainException">The order cannot receive goods, or the receipt is invalid.</exception>
    public void Receive(IReadOnlyCollection<ReceiptLine> receipt, DateTimeOffset receivedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        if (Status is not (PurchaseOrderStatus.Placed or PurchaseOrderStatus.PartiallyReceived))
        {
            throw new DomainException("purchase_order.not_receivable", "Goods can be received only on a placed or partially received order.");
        }

        if (receipt.Count == 0)
        {
            throw new DomainException("purchase_order.receipt_empty", "A receipt must receive at least one line.");
        }

        var validReceivedAtUtc = RequireUtc(receivedAtUtc);

        var seen = new HashSet<Guid>();
        foreach (var received in receipt)
        {
            ArgumentNullException.ThrowIfNull(received);

            if (!seen.Add(received.LineId))
            {
                throw new DomainException("purchase_order.receipt_line_duplicate", "A line can appear only once in one receipt.");
            }

            var line = FindLine(received.LineId);

            if (received.Quantity.Value == 0m)
            {
                throw new DomainException("purchase_order.receipt_quantity_required", "A received quantity must be greater than zero.");
            }

            if (received.Quantity.Value > line.OutstandingQuantity.Value)
            {
                throw new DomainException("purchase_order.receipt_exceeds_ordered", "A line cannot receive more than is still outstanding.");
            }
        }

        foreach (var received in receipt)
        {
            FindLine(received.LineId).Receive(received.Quantity);
        }

        UpdatedAtUtc = validReceivedAtUtc;
        if (_lines.All(line => line.IsFullyReceived))
        {
            Status = PurchaseOrderStatus.Received;
            ClosedAtUtc = validReceivedAtUtc;
        }
        else
        {
            Status = PurchaseOrderStatus.PartiallyReceived;
        }
    }

    /// <summary>Abandons the order: <c>Draft</c> or <c>Placed</c> → <c>Cancelled</c>. An order that has received anything cannot be cancelled.</summary>
    /// <exception cref="DomainException">The order has received goods or is already closed.</exception>
    public void Cancel(DateTimeOffset cancelledAtUtc)
    {
        if (Status is not (PurchaseOrderStatus.Draft or PurchaseOrderStatus.Placed))
        {
            throw new DomainException("purchase_order.cannot_cancel", "Only a draft or placed order that has received nothing can be cancelled.");
        }

        var validCancelledAtUtc = RequireUtc(cancelledAtUtc);

        Status = PurchaseOrderStatus.Cancelled;
        ClosedAtUtc = validCancelledAtUtc;
        UpdatedAtUtc = validCancelledAtUtc;
    }

    private void RequireDraft()
    {
        if (Status != PurchaseOrderStatus.Draft)
        {
            throw new DomainException("purchase_order.not_draft", "The order can be changed only while it is a draft.");
        }
    }

    private PurchaseOrderLine FindLine(Guid lineId) =>
        _lines.SingleOrDefault(line => line.Id == lineId)
        ?? throw new DomainException("purchase_order.line_not_found", "The order has no such line.");

    private void ValidLineValues(Quantity orderedQuantity, Money unitCost)
    {
        if (orderedQuantity.Value == 0m)
        {
            throw new DomainException("purchase_order.line_quantity_required", "The ordered quantity must be greater than zero.");
        }

        if (unitCost.Amount < 0m)
        {
            throw new DomainException("purchase_order.line_unit_cost_negative", "The unit cost cannot be negative.");
        }

        if (unitCost.Currency != Currency)
        {
            throw new DomainException("purchase_order.line_currency_mismatch", "The unit cost must be in the currency of the order.");
        }
    }

    private void EnsureTotalFits(IEnumerable<PurchaseOrderLine> lines) =>
        _ = lines.Aggregate(Money.Zero(Currency), (sum, line) => sum.Add(line.LineTotal));

    private static string Snapshot(string? value, int maxLength, string name)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainException($"purchase_order.line_{name}_required", $"The product's {name.Replace('_', ' ')} is required.");
        }

        if (trimmed.Length > maxLength)
        {
            throw new DomainException($"purchase_order.line_{name}_too_long", $"The product's {name.Replace('_', ' ')} can have at most {maxLength} characters.");
        }

        return trimmed;
    }

    private static string? ValidReference(string? reference)
    {
        var trimmed = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();

        if (trimmed is { Length: > MaxReferenceLength })
        {
            throw new DomainException("purchase_order.reference_too_long", $"The reference can have at most {MaxReferenceLength} characters.");
        }

        return trimmed;
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new DomainException("purchase_order.timestamp_not_utc", "Timestamps must be in UTC.");
        }

        return value;
    }
}
