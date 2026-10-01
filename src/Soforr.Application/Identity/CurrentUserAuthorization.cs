using Soforr.Application.Common;

namespace Soforr.Application.Identity;

public static class CurrentUserAuthorization
{
    /// <summary>
    /// The explicit permission check at the top of a protected handler (ADR 0002, ADR 0003). Returns null when the
    /// caller may proceed, otherwise the error to return: not signed in is <c>identity.not_authenticated</c> (401);
    /// signed in without a membership or without the permission is <c>identity.forbidden</c> (403). Being signed in
    /// grants nothing by itself.
    /// </summary>
    public static Error? Require(this ICurrentUser user, Permission permission)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.UserId is null)
        {
            return IdentityErrors.NotAuthenticated;
        }

        if (user.OrganizationId is null || user.Role is not { } role || !RolePermissions.IsGranted(role, permission))
        {
            return IdentityErrors.Forbidden;
        }

        return null;
    }
}
