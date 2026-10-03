using Soforr.Domain.Audit;

namespace Soforr.Application.Audit;

/// <summary>An audit event as it is returned to callers; the Domain entity is never exposed.</summary>
public sealed record AuditEventDto(
    Guid Id,
    string Action,
    Guid? ActorId,
    string? EntityType,
    Guid? EntityId,
    string? Description,
    string? CorrelationId,
    DateTimeOffset OccurredAtUtc)
{
    internal static AuditEventDto From(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        return new AuditEventDto(
            auditEvent.Id,
            auditEvent.Action,
            auditEvent.ActorId,
            auditEvent.EntityType,
            auditEvent.EntityId,
            auditEvent.Description,
            auditEvent.CorrelationId,
            auditEvent.OccurredAtUtc);
    }
}
