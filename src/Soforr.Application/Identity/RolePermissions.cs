using Soforr.Domain.Identity;

namespace Soforr.Application.Identity;

/// <summary>
/// The fixed role-to-permission mapping of the MVP (ADR 0002). It lives in code so it is versioned, reviewed and
/// tested; roles are not editable. Anything not listed here is denied.
/// </summary>
public static class RolePermissions
{
    private static readonly Dictionary<Role, HashSet<Permission>> Grants = new()
    {
        [Role.Owner] = [Permission.OrganizationView, Permission.OrganizationManageSettings, Permission.ProductView, Permission.ProductViewCost, Permission.ProductManage, Permission.AuditView, Permission.InventoryView, Permission.InventoryViewHistory, Permission.InventoryAdjust],
        [Role.Manager] = [Permission.OrganizationView, Permission.ProductView, Permission.ProductViewCost, Permission.ProductManage, Permission.InventoryView, Permission.InventoryViewHistory, Permission.InventoryAdjust],
        [Role.Staff] = [Permission.OrganizationView, Permission.ProductView, Permission.InventoryView],
    };

    public static bool IsGranted(Role role, Permission permission) =>
        Grants.TryGetValue(role, out var permissions) && permissions.Contains(permission);
}
