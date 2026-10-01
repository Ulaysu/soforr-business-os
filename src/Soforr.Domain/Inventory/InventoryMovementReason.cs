namespace Soforr.Domain.Inventory;

/// <summary>
/// Why stock changed. Every movement has exactly one reason. Values start at 1 so an uninitialized value is never a
/// valid reason. <c>Transfer</c> is intentionally absent: the MVP has a single stock location per organization.
/// </summary>
public enum InventoryMovementReason
{
    PurchaseReceived = 1,
    SaleCompleted = 2,
    ManualAdjustment = 3,
    Return = 4,
    Damage = 5,
}
