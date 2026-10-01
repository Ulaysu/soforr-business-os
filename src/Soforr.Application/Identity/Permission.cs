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
}
