namespace Soforr.Domain.Identity;

/// <summary>
/// The fixed roles of the MVP (ADR 0002). What each role may do is defined by the permission mapping that arrives
/// with IDENTITY-002; this enum only names them.
/// </summary>
public enum Role
{
    Owner,
    Manager,
    Staff,
}
