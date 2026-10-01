namespace Soforr.Domain.Inventory;

/// <summary>
/// The kind of record a movement originates from. Together with the source id, the product and the reason it
/// identifies a movement so a retried operation can be recognized and recorded only once. Values start at 1.
/// </summary>
public enum InventorySourceType
{
    PurchaseReceipt = 1,
    Sale = 2,
    Adjustment = 3,
}
