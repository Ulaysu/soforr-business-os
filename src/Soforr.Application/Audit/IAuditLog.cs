namespace Soforr.Application.Audit;

/// <summary>What to record: an action on an optional record, by an optional actor, within one organization.</summary>
public sealed record AuditEntry(
    Guid OrganizationId,
    string Action,
    Guid? ActorId,
    string? EntityType = null,
    Guid? EntityId = null,
    string? Description = null);

/// <summary>
/// Records business-sensitive facts (ADR 0003: audit events are added to the same unit of work as the change, so they
/// commit or fail together with it). <see cref="Record"/> only adds the event to the current unit of work; the
/// command's single <c>SaveChangesAsync</c> stores it. There is deliberately no way to read, change or delete an event
/// here: reading is the owner-only <c>ListAuditEvents</c> query, and persistence refuses updates and deletes.
/// The time, the identifier and the request's correlation ID are supplied by the log, not by the caller.
/// </summary>
public interface IAuditLog
{
    /// <exception cref="Soforr.Domain.Common.DomainException">The entry is invalid; this is a programming error, not bad user input.</exception>
    void Record(AuditEntry entry);
}
