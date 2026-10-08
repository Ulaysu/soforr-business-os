using Soforr.Application.Common;
using Soforr.Application.Identity;

namespace Soforr.Application.Offline;

internal static class OfflineAccess
{
    /// <summary>
    /// Whether the caller may hold an offline grant: signed in, with a membership, and allowed to record sales or payments (the two
    /// offline workflows). Holding a grant authorizes nothing else; each replayed operation checks its own permission.
    /// </summary>
    public static Error? Denied(ICurrentUser user) =>
        user.Has(Permission.SalesCreate) || user.Has(Permission.PaymentsRecord) ? null : user.Require(Permission.SalesCreate);
}
