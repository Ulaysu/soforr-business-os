using Soforr.Domain.Common;

namespace Soforr.Domain.Purchasing;

/// <summary>What one receipt received on one order line: the line, its product at that time, and the amount.</summary>
public sealed record PurchaseReceiptEntry(Guid LineId, Guid ProductId, Quantity Quantity);

/// <summary>
/// The record of one successful receipt of goods against one order: which order, which lines and how much of each. It
/// exists so that a receipt id means exactly one receipt. Sending the same id again with the same order, lines and
/// quantities is a repeat; the same id with anything different is a conflict (<see cref="IsSameAs"/>). It is written in
/// the same transaction as the order's received quantities and the stock movements, so a receipt that did not commit
/// leaves nothing behind and does not reserve its id. A receipt is never changed or deleted.
/// </summary>
public sealed class PurchaseReceipt
{
    private readonly List<PurchaseReceiptLine> _lines = [];

    // Required by the persistence layer for materialization; not part of the domain API.
    private PurchaseReceipt()
    {
    }

    private PurchaseReceipt(Guid id, Guid organizationId, Guid purchaseOrderId, Guid receivedByUserId, DateTimeOffset receivedAtUtc, IEnumerable<PurchaseReceiptEntry> entries)
    {
        Id = id;
        OrganizationId = organizationId;
        PurchaseOrderId = purchaseOrderId;
        ReceivedByUserId = receivedByUserId;
        ReceivedAtUtc = receivedAtUtc;
        _lines.AddRange(entries.Select(entry => new PurchaseReceiptLine(organizationId, id, entry.LineId, entry.ProductId, entry.Quantity)));
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid PurchaseOrderId { get; private set; }

    public Guid ReceivedByUserId { get; private set; }

    public DateTimeOffset ReceivedAtUtc { get; private set; }

    public IReadOnlyList<PurchaseReceiptLine> Lines => _lines;

    /// <exception cref="DomainException">An invariant is violated.</exception>
    public static PurchaseReceipt Create(Guid id, Guid organizationId, Guid purchaseOrderId, Guid receivedByUserId, DateTimeOffset receivedAtUtc, IReadOnlyCollection<PurchaseReceiptEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (id == Guid.Empty)
        {
            throw new DomainException("purchase_receipt.id_required", "A receipt must have an identifier.");
        }

        if (organizationId == Guid.Empty || purchaseOrderId == Guid.Empty || receivedByUserId == Guid.Empty)
        {
            throw new DomainException("purchase_receipt.owner_required", "A receipt must belong to an organization, an order and a user.");
        }

        if (receivedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new DomainException("purchase_receipt.timestamp_not_utc", "Timestamps must be in UTC.");
        }

        if (entries.Count == 0)
        {
            throw new DomainException("purchase_receipt.lines_required", "A receipt must have at least one line.");
        }

        if (entries.Any(entry => entry is null || entry.Quantity.Value == 0m)
            || entries.Select(entry => entry.LineId).Distinct().Count() != entries.Count)
        {
            throw new DomainException("purchase_receipt.lines_invalid", "A receipt's lines must be distinct and each must receive more than zero.");
        }

        return new PurchaseReceipt(id, organizationId, purchaseOrderId, receivedByUserId, receivedAtUtc, entries);
    }

    /// <summary>
    /// Whether a request is exactly this receipt again: the same order, the same set of lines (no more, no fewer) and the
    /// same quantity on each. Quantities are compared by value, so 5 and 5.0 are the same.
    /// </summary>
    public bool IsSameAs(Guid purchaseOrderId, IReadOnlyCollection<ReceiptLine> requested)
    {
        ArgumentNullException.ThrowIfNull(requested);

        if (purchaseOrderId != PurchaseOrderId || requested.Count != _lines.Count)
        {
            return false;
        }

        // Equal counts, a line named twice in the request, or a line that is not on the receipt all make it a different request.
        return requested.Select(line => line.LineId).Distinct().Count() == requested.Count
            && requested.All(line => _lines.Any(stored => stored.LineId == line.LineId && stored.Quantity.Value == line.Quantity.Value));
    }
}

/// <summary>One line of a <see cref="PurchaseReceipt"/>.</summary>
public sealed class PurchaseReceiptLine
{
    // Required by the persistence layer for materialization; not part of the domain API.
    private PurchaseReceiptLine()
    {
        Quantity = null!;
    }

    internal PurchaseReceiptLine(Guid organizationId, Guid receiptId, Guid lineId, Guid productId, Quantity quantity)
    {
        OrganizationId = organizationId;
        ReceiptId = receiptId;
        LineId = lineId;
        ProductId = productId;
        Quantity = quantity;
    }

    public Guid OrganizationId { get; private set; }

    public Guid ReceiptId { get; private set; }

    /// <summary>The order line that received goods.</summary>
    public Guid LineId { get; private set; }

    /// <summary>The line's product, recorded so the receipt stands on its own.</summary>
    public Guid ProductId { get; private set; }

    public Quantity Quantity { get; private set; }
}
