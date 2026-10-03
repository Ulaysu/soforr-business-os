using System.Text.RegularExpressions;
using Soforr.Domain.Common;

namespace Soforr.Domain.Audit;

/// <summary>
/// One recorded fact about something business-sensitive that happened: who did what, to which record, when, and in
/// which request. Audit events are append-only: once recorded they never change and are never deleted, so there are
/// no setters and no mutating methods, and persistence refuses updates and deletes. An event belongs to exactly one
/// organization. It records that something happened; it is never the record of the business data itself.
/// It deliberately holds no personal data such as an e-mail address or a password: it refers to people and records by
/// their identifiers.
/// </summary>
public sealed partial class AuditEvent
{
    public const int MaxActionLength = 100;
    public const int MaxEntityTypeLength = 50;
    public const int MaxDescriptionLength = 500;
    public const int MaxCorrelationIdLength = 64;

    // Required by the persistence layer for materialization; not part of the domain API.
    private AuditEvent()
    {
        Action = string.Empty;
    }

    private AuditEvent(
        Guid id,
        Guid organizationId,
        Guid? actorId,
        string action,
        string? entityType,
        Guid? entityId,
        string? description,
        string? correlationId,
        DateTimeOffset occurredAtUtc)
    {
        Id = id;
        OrganizationId = organizationId;
        ActorId = actorId;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        Description = description;
        CorrelationId = correlationId;
        OccurredAtUtc = occurredAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>The user who caused the event; null for something the system did.</summary>
    public Guid? ActorId { get; private set; }

    /// <summary>What happened, as a stable dotted code in lower case, for example <c>auth.sign_in_succeeded</c>.</summary>
    public string Action { get; private set; }

    /// <summary>The kind of record the event is about (for example <c>Membership</c>); set together with <see cref="EntityId"/>.</summary>
    public string? EntityType { get; private set; }

    public Guid? EntityId { get; private set; }

    /// <summary>A short human-readable note, never personal data. Optional.</summary>
    public string? Description { get; private set; }

    /// <summary>The correlation ID of the request that caused the event, to find its log lines. Optional.</summary>
    public string? CorrelationId { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <exception cref="DomainException">An invariant is violated.</exception>
    public static AuditEvent Record(
        Guid id,
        Guid organizationId,
        Guid? actorId,
        string? action,
        string? entityType,
        Guid? entityId,
        string? description,
        string? correlationId,
        DateTimeOffset occurredAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("audit_event.id_required", "An audit event must have an identifier.");
        }

        if (organizationId == Guid.Empty)
        {
            throw new DomainException("audit_event.organization_required", "An audit event must belong to an organization.");
        }

        if (actorId == Guid.Empty)
        {
            throw new DomainException("audit_event.actor_invalid", "The actor must be a user or absent.");
        }

        var validAction = action?.Trim();
        if (string.IsNullOrEmpty(validAction) || validAction.Length > MaxActionLength || !ActionPattern().IsMatch(validAction))
        {
            throw new DomainException("audit_event.action_invalid", "The action must be a dotted lower-case code such as 'auth.sign_in_succeeded'.");
        }

        var validType = string.IsNullOrWhiteSpace(entityType) ? null : entityType.Trim();
        if (validType is { Length: > MaxEntityTypeLength })
        {
            throw new DomainException("audit_event.entity_type_too_long", $"The entity type can have at most {MaxEntityTypeLength} characters.");
        }

        if (entityId == Guid.Empty || (validType is null) != (entityId is null))
        {
            throw new DomainException("audit_event.entity_reference_invalid", "The entity type and the entity id must be given together.");
        }

        var validDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (validDescription is { Length: > MaxDescriptionLength })
        {
            throw new DomainException("audit_event.description_too_long", $"The description can have at most {MaxDescriptionLength} characters.");
        }

        var validCorrelationId = string.IsNullOrWhiteSpace(correlationId) ? null : correlationId.Trim();
        if (validCorrelationId is { Length: > MaxCorrelationIdLength })
        {
            throw new DomainException("audit_event.correlation_id_too_long", $"The correlation id can have at most {MaxCorrelationIdLength} characters.");
        }

        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new DomainException("audit_event.timestamp_not_utc", "Timestamps must be in UTC.");
        }

        return new AuditEvent(id, organizationId, actorId, validAction, validType, entityId, validDescription, validCorrelationId, occurredAtUtc);
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex ActionPattern();
}
