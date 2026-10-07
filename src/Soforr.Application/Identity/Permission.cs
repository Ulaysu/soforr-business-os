using System.Diagnostics.CodeAnalysis;

namespace Soforr.Application.Identity;

/// <summary>
/// A named thing a user may be allowed to do (ADR 0002). Each protected use case requires exactly one permission,
/// checked at the top of its handler. Roles are granted permissions in <see cref="RolePermissions"/>. A new
/// permission is added together with the use case that needs it, and the matrix in docs/security/security.md.
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "'Permission' is the domain term used by ADR 0002; the rule targets System.Security.IPermission.")]
public enum Permission
{
    /// <summary>Read the settings of the user's own organization.</summary>
    OrganizationView,

    /// <summary>Change the name and time zone of the user's own organization.</summary>
    OrganizationManageSettings,

    /// <summary>Read and search the organization's products (without their cost price).</summary>
    ProductView,

    /// <summary>See the cost price of products. Cost is margin information, so it is granted separately from viewing.</summary>
    ProductViewCost,

    /// <summary>Create, change and deactivate products.</summary>
    ProductManage,

    /// <summary>Read the organization's audit trail (who did what and when).</summary>
    AuditView,

    /// <summary>Read stock levels and the low-stock list.</summary>
    InventoryView,

    /// <summary>Read the movement history of a product (who changed its stock, when and why).</summary>
    InventoryViewHistory,

    /// <summary>Adjust stock by hand (count corrections, found or lost stock).</summary>
    InventoryAdjust,

    /// <summary>Read suppliers and purchase orders. Orders carry unit costs, so this is not granted to Staff.</summary>
    PurchasingView,

    /// <summary>Create suppliers, and create, place and cancel purchase orders.</summary>
    PurchasingManage,

    /// <summary>Record goods received against a purchase order, which adds the stock.</summary>
    PurchasingReceive,

    /// <summary>Read sales. The cost price on a sale line is shown only with <see cref="ProductViewCost"/>.</summary>
    SalesView,

    /// <summary>Record a sale, which removes the sold stock. A cashier needs it, so Staff has it.</summary>
    SalesCreate,

    /// <summary>Void a sale after the fact, which returns the stock it took.</summary>
    SalesVoid,

    /// <summary>See the insights ("things that need your attention"). Insights that reveal costs additionally need <see cref="ProductViewCost"/>. Not granted to Staff.</summary>
    InsightsView,
}
