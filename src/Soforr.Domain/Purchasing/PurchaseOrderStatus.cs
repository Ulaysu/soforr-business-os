namespace Soforr.Domain.Purchasing;

/// <summary>
/// Where a purchase order is in its life. The only allowed transitions are:
/// <c>Draft → Placed → PartiallyReceived → Received</c> (a placed order that is received in full at once goes straight to
/// <c>Received</c>), and <c>Draft</c> or <c>Placed</c> → <c>Cancelled</c>. <c>Received</c> and <c>Cancelled</c> are final.
/// The names are stored as text, so a value is never renamed or renumbered.
/// </summary>
public enum PurchaseOrderStatus
{
    /// <summary>Being prepared: lines can be added, changed and removed. Nothing has been ordered yet.</summary>
    Draft = 1,

    /// <summary>Sent to the supplier: the lines are fixed; goods can be received.</summary>
    Placed = 2,

    /// <summary>Some, but not all, of the ordered quantity has been received.</summary>
    PartiallyReceived = 3,

    /// <summary>Everything ordered has been received.</summary>
    Received = 4,

    /// <summary>Abandoned before anything was received.</summary>
    Cancelled = 5,
}
