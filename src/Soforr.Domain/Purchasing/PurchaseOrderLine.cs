using Soforr.Domain.Common;

namespace Soforr.Domain.Purchasing;

/// <summary>
/// One product on a purchase order. It records the product's SKU, name and unit of measure <b>as they were when the line
/// was added</b> (a snapshot, not a reference to the product's current values) and the unit cost agreed, because a
/// purchase describes what happened and products stay editable. A line is created and changed only through its
/// <see cref="PurchaseOrder"/>, which enforces the rules that involve the whole order.
/// </summary>
public sealed class PurchaseOrderLine
{
    public const int MaxSkuLength = 64;
    public const int MaxNameLength = 200;
    public const int MaxUnitOfMeasureLength = 32;

    // Required by the persistence layer for materialization; not part of the domain API.
    private PurchaseOrderLine()
    {
        SkuSnapshot = string.Empty;
        NameSnapshot = string.Empty;
        UnitOfMeasureSnapshot = string.Empty;
        OrderedQuantity = null!;
        ReceivedQuantity = null!;
        UnitCost = null!;
    }

    internal PurchaseOrderLine(
        Guid id,
        Guid organizationId,
        Guid purchaseOrderId,
        Guid productId,
        string skuSnapshot,
        string nameSnapshot,
        string unitOfMeasureSnapshot,
        Quantity orderedQuantity,
        Money unitCost)
    {
        Id = id;
        OrganizationId = organizationId;
        PurchaseOrderId = purchaseOrderId;
        ProductId = productId;
        SkuSnapshot = skuSnapshot;
        NameSnapshot = nameSnapshot;
        UnitOfMeasureSnapshot = unitOfMeasureSnapshot;
        OrderedQuantity = orderedQuantity;
        ReceivedQuantity = Quantity.Zero;
        UnitCost = unitCost;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid PurchaseOrderId { get; private set; }

    public Guid ProductId { get; private set; }

    /// <summary>The product's SKU when the line was added.</summary>
    public string SkuSnapshot { get; private set; }

    /// <summary>The product's name when the line was added.</summary>
    public string NameSnapshot { get; private set; }

    /// <summary>The product's unit of measure when the line was added.</summary>
    public string UnitOfMeasureSnapshot { get; private set; }

    /// <summary>How much was ordered. Always greater than zero.</summary>
    public Quantity OrderedQuantity { get; private set; }

    /// <summary>How much has been received so far: between zero and <see cref="OrderedQuantity"/>.</summary>
    public Quantity ReceivedQuantity { get; private set; }

    /// <summary>The cost of one unit as agreed on this order, in the order's currency. Zero is allowed (a free sample).</summary>
    public Money UnitCost { get; private set; }

    /// <summary>What is still to be received.</summary>
    public Quantity OutstandingQuantity => Quantity.Create(OrderedQuantity.Value - ReceivedQuantity.Value);

    public bool IsFullyReceived => ReceivedQuantity.Value == OrderedQuantity.Value;

    /// <summary>The cost of everything ordered on this line: unit cost times the ordered quantity (rounding policy: <see cref="Money.Multiply"/>).</summary>
    public Money LineTotal => UnitCost.Multiply(OrderedQuantity);

    internal void Change(Quantity orderedQuantity, Money unitCost)
    {
        OrderedQuantity = orderedQuantity;
        UnitCost = unitCost;
    }

    internal void Receive(Quantity quantity)
    {
        ReceivedQuantity = Quantity.Create(ReceivedQuantity.Value + quantity.Value);
    }
}
