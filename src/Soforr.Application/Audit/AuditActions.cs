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
}
