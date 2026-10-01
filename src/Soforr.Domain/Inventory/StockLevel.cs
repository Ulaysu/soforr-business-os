using Soforr.Domain.Common;

namespace Soforr.Domain.Inventory;

/// <summary>
/// The current quantity of one product in one organization (the MVP has a single stock location). The quantity is
/// derived from movements: it changes only by applying a recorded <see cref="InventoryMovement"/>, so it always
/// equals the sum of the movements applied to it. Stock cannot go negative; a movement that would do so is
/// rejected and nothing changes.
/// The concurrency token that makes concurrent updates safe is a persistence concern (ADR 0004) and is not part of
/// this entity. Allowing negative stock would be a separate, explicitly approved feature.
/// </summary>
public sealed class StockLevel
{
    // Required by the persistence layer for materialization; not part of the domain API.
    private StockLevel()
    {
        Quantity = null!;
    }

    private StockLevel(Guid organizationId, Guid productId, DateTimeOffset openedAtUtc)
    {
        OrganizationId = organizationId;
        ProductId = productId;
        Quantity = Quantity.Zero;
        UpdatedAtUtc = openedAtUtc;
    }

    public Guid OrganizationId { get; private set; }

    public Guid ProductId { get; private set; }

    public Quantity Quantity { get; private set; }

    /// <summary>When the last applied movement occurred; the opening time if none has been applied.</summary>
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Starts tracking a product with no stock.</summary>
    /// <exception cref="DomainException">An invariant is violated.</exception>
    public static StockLevel Open(Guid organizationId, Guid productId, DateTimeOffset openedAtUtc)
    {
        if (organizationId == Guid.Empty)
        {
            throw new DomainException("stock_level.organization_required", "A stock level must belong to an organization.");
        }

        if (productId == Guid.Empty)
        {
            throw new DomainException("stock_level.product_required", "A stock level must be for a product.");
        }

        if (openedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new DomainException("stock_level.timestamp_not_utc", "Timestamps must be in UTC.");
        }

        return new StockLevel(organizationId, productId, openedAtUtc);
    }

    /// <summary>
    /// Applies a recorded movement to the quantity. The movement must be for this product in this organization.
    /// If it is rejected, the stock level is unchanged.
    /// </summary>
    /// <exception cref="DomainException">The movement is for another product or organization, would make stock negative, or overflows the quantity.</exception>
    public void Apply(InventoryMovement movement)
    {
        ArgumentNullException.ThrowIfNull(movement);

        if (movement.OrganizationId != OrganizationId || movement.ProductId != ProductId)
        {
            throw new DomainException("stock_level.movement_mismatch", "The movement is for a different product or organization.");
        }

        var newQuantity = Quantity.Value + movement.QuantityDelta.Value;

        if (newQuantity < 0m)
        {
            throw new DomainException("stock_level.insufficient_stock", "There is not enough stock for this movement.");
        }

        var validQuantity = Quantity.Create(newQuantity);

        Quantity = validQuantity;
        UpdatedAtUtc = movement.OccurredAtUtc;
    }
}
