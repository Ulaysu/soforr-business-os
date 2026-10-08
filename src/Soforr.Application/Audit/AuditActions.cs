namespace Soforr.Application.Audit;

/// <summary>
/// The stable codes of the audited actions. A code never changes meaning once released: the audit trail outlives the
/// code. Add one here with the use case that records it.
/// </summary>
public static class AuditActions
{
    /// <summary>A user registered and created an organization.</summary>
    public const string UserRegistered = "user.registered";

    /// <summary>A role was assigned to a user within the organization (at registration, the Owner role).</summary>
    public const string RoleAssigned = "membership.role_assigned";

    public const string SignInSucceeded = "auth.sign_in_succeeded";

    /// <summary>
    /// An account was locked by repeated wrong passwords. Recorded once when the lock starts, not for every refused
    /// attempt: an anonymous caller must not be able to make the system write unlimited rows.
    /// </summary>
    public const string AccountLocked = "auth.account_locked";

    public const string SignedOut = "auth.signed_out";

    /// <summary>Stock was adjusted by hand. The entity is the movement; the description states the change, never the note.</summary>
    public const string StockAdjusted = "inventory.stock_adjusted";

    public const string SupplierCreated = "purchasing.supplier_created";

    public const string PurchaseOrderCreated = "purchasing.order_created";

    public const string PurchaseOrderPlaced = "purchasing.order_placed";

    public const string PurchaseOrderCancelled = "purchasing.order_cancelled";

    /// <summary>Goods were received against an order and stock increased. The entity is the order.</summary>
    public const string PurchaseReceived = "purchasing.goods_received";

    /// <summary>A sale was recorded and its stock removed. The entity is the sale.</summary>
    public const string SaleCreated = "sales.sale_created";

    /// <summary>A sale was recorded with at least one line charged at a price different from the catalog price. The entity is the sale; the description carries counts and amounts, never the reasons.</summary>
    public const string SalePriceOverridden = "sales.sale_price_overridden";

    /// <summary>A sale was voided and its stock returned. The entity is the sale; the description never carries the reason text.</summary>
    public const string SaleVoided = "sales.sale_voided";

    /// <summary>A customer was created. The entity is the customer; the description never carries personal data.</summary>
    public const string CustomerCreated = "customers.customer_created";

    /// <summary>A customer's details were changed. The entity is the customer; the description never carries personal data.</summary>
    public const string CustomerUpdated = "customers.customer_updated";

    public const string CustomerDeactivated = "customers.customer_deactivated";

    /// <summary>A payment was recorded against a sale. The entity is the sale; the description gives the amount, never the method or the customer.</summary>
    public const string PaymentRecorded = "sales.payment_recorded";

    /// <summary>A payment was reversed. The entity is the sale; the description never carries the reason text.</summary>
    public const string PaymentReversed = "sales.payment_reversed";
}
