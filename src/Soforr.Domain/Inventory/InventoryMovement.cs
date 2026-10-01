using Soforr.Domain.Common;

namespace Soforr.Domain.Inventory;

/// <summary>
/// One recorded change to a product's stock. Movements are append-only: once recorded they never change and are
/// never deleted; a correction is a new movement with its own reason. There are therefore no setters and no
/// mutating methods. Every movement says why stock changed (reason), where it came from (source type and id) and
/// who caused it (actor).
/// The reason gives the stock change its business meaning, so it fixes the sign of the change: a purchase receipt
/// and a return add stock, a sale and damage remove it, and a manual adjustment may do either (it can discover an
/// excess or a shortage).
/// That the same source cannot record the same movement twice is a rule across movements, so the database enforces
/// it (ADR 0004); the entity only carries the identifying fields.
/// </summary>
public sealed class InventoryMovement
{
    public const int MaxNoteLength = 500;

    // Required by the persistence layer for materialization; not part of the domain API.
    private InventoryMovement()
    {
        QuantityDelta = null!;
    }

    private InventoryMovement(
        Guid id,
        Guid organizationId,
        Guid productId,
        QuantityDelta quantityDelta,
        InventoryMovementReason reason,
        InventorySourceType sourceType,
        Guid sourceId,
        Guid actorId,
        string? note,
        DateTimeOffset occurredAtUtc)
    {
        Id = id;
        OrganizationId = organizationId;
        ProductId = productId;
        QuantityDelta = quantityDelta;
        Reason = reason;
        SourceType = sourceType;
        SourceId = sourceId;
        ActorId = actorId;
        Note = note;
        OccurredAtUtc = occurredAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid ProductId { get; private set; }

    public QuantityDelta QuantityDelta { get; private set; }

    public InventoryMovementReason Reason { get; private set; }

    public InventorySourceType SourceType { get; private set; }

    public Guid SourceId { get; private set; }

    public Guid ActorId { get; private set; }

    /// <summary>Free text. Required for a manual adjustment; optional otherwise.</summary>
    public string? Note { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <exception cref="DomainException">An invariant is violated, including a change whose sign does not match its reason.</exception>
    public static InventoryMovement Record(
        Guid id,
        Guid organizationId,
        Guid productId,
        QuantityDelta quantityDelta,
        InventoryMovementReason reason,
        InventorySourceType sourceType,
        Guid sourceId,
        Guid actorId,
        string? note,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(quantityDelta);

        Require(id, "inventory_movement.id_required", "A movement must have an identifier.");
        Require(organizationId, "inventory_movement.organization_required", "A movement must belong to an organization.");
        Require(productId, "inventory_movement.product_required", "A movement must be for a product.");
        Require(sourceId, "inventory_movement.source_required", "A movement must reference the record it comes from.");
        Require(actorId, "inventory_movement.actor_required", "A movement must record who caused it.");

        if (!Enum.IsDefined(reason))
        {
            throw new DomainException("inventory_movement.reason_invalid", "A movement must have a valid reason.");
        }

        if (!Enum.IsDefined(sourceType))
        {
            throw new DomainException("inventory_movement.source_type_invalid", "A movement must have a valid source type.");
        }

        EnsureSignMatchesReason(quantityDelta, reason);

        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new DomainException("inventory_movement.timestamp_not_utc", "Timestamps must be in UTC.");
        }

        return new InventoryMovement(
            id,
            organizationId,
            productId,
            quantityDelta,
            reason,
            sourceType,
            sourceId,
            actorId,
            ValidNote(note, reason),
            occurredAtUtc);
    }

    private static void EnsureSignMatchesReason(QuantityDelta quantityDelta, InventoryMovementReason reason)
    {
        switch (reason)
        {
            case InventoryMovementReason.PurchaseReceived:
            case InventoryMovementReason.Return:
                if (quantityDelta.Value < 0m)
                {
                    throw new DomainException("inventory_movement.delta_must_be_positive", $"A {reason} movement must add stock.");
                }

                break;

            case InventoryMovementReason.SaleCompleted:
            case InventoryMovementReason.Damage:
                if (quantityDelta.Value > 0m)
                {
                    throw new DomainException("inventory_movement.delta_must_be_negative", $"A {reason} movement must remove stock.");
                }

                break;

            case InventoryMovementReason.ManualAdjustment:
                // An adjustment can discover either an excess or a shortage, so either sign is valid.
                break;
        }
    }

    private static string? ValidNote(string? note, InventoryMovementReason reason)
    {
        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        if (trimmed is null && reason == InventoryMovementReason.ManualAdjustment)
        {
            throw new DomainException("inventory_movement.note_required", "A manual adjustment requires a note explaining it.");
        }

        if (trimmed is not null && trimmed.Length > MaxNoteLength)
        {
            throw new DomainException("inventory_movement.note_too_long", $"The note can have at most {MaxNoteLength} characters.");
        }

        return trimmed;
    }

    private static void Require(Guid value, string code, string message)
    {
        if (value == Guid.Empty)
        {
            throw new DomainException(code, message);
        }
    }
}
