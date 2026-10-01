using System.Reflection;
using Soforr.Domain.Common;
using Soforr.Domain.Inventory;

namespace Soforr.Domain.Tests.Inventory;

public class InventoryMovementTests
{
    private static readonly Guid MovementId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c1a20");
    private static readonly Guid OrganizationId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c1a10");
    private static readonly Guid ProductId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c1a11");
    private static readonly Guid SourceId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c1a30");
    private static readonly Guid ActorId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c1a40");
    private static readonly DateTimeOffset At = new(2026, 4, 2, 10, 15, 0, TimeSpan.Zero);

    private static InventoryMovement Record(
        InventoryMovementReason reason = InventoryMovementReason.PurchaseReceived,
        string? note = null,
        decimal? delta = null,
        Guid? id = null,
        Guid? organizationId = null,
        Guid? productId = null,
        Guid? sourceId = null,
        Guid? actorId = null,
        InventorySourceType sourceType = InventorySourceType.PurchaseReceipt,
        DateTimeOffset? at = null) =>
        InventoryMovement.Record(
            id ?? MovementId,
            organizationId ?? OrganizationId,
            productId ?? ProductId,
            QuantityDelta.Create(delta ?? ValidDeltaFor(reason)),
            reason,
            sourceType,
            sourceId ?? SourceId,
            actorId ?? ActorId,
            note,
            at ?? At);

    // A valid amount for the reason: sales and damage remove stock, everything else adds it.
    private static decimal ValidDeltaFor(InventoryMovementReason reason) =>
        reason is InventoryMovementReason.SaleCompleted or InventoryMovementReason.Damage ? -10m : 10m;

    [Fact]
    public void Record_KeepsEveryFieldItWasGiven()
    {
        var movement = Record(InventoryMovementReason.SaleCompleted, "Sold at the counter", -3m, sourceType: InventorySourceType.Sale);

        Assert.Equal(MovementId, movement.Id);
        Assert.Equal(OrganizationId, movement.OrganizationId);
        Assert.Equal(ProductId, movement.ProductId);
        Assert.Equal(QuantityDelta.Create(-3m), movement.QuantityDelta);
        Assert.Equal(InventoryMovementReason.SaleCompleted, movement.Reason);
        Assert.Equal(InventorySourceType.Sale, movement.SourceType);
        Assert.Equal(SourceId, movement.SourceId);
        Assert.Equal(ActorId, movement.ActorId);
        Assert.Equal("Sold at the counter", movement.Note);
        Assert.Equal(At, movement.OccurredAtUtc);
    }

    [Fact]
    public void Record_RequiresEveryIdentifier()
    {
        Assert.Equal("inventory_movement.id_required", Assert.Throws<DomainException>(() => Record(id: Guid.Empty)).Code);
        Assert.Equal("inventory_movement.organization_required", Assert.Throws<DomainException>(() => Record(organizationId: Guid.Empty)).Code);
        Assert.Equal("inventory_movement.product_required", Assert.Throws<DomainException>(() => Record(productId: Guid.Empty)).Code);
        Assert.Equal("inventory_movement.source_required", Assert.Throws<DomainException>(() => Record(sourceId: Guid.Empty)).Code);
        Assert.Equal("inventory_movement.actor_required", Assert.Throws<DomainException>(() => Record(actorId: Guid.Empty)).Code);
    }

    [Fact]
    public void Record_RequiresAQuantityDelta()
    {
        Assert.Throws<ArgumentNullException>(() => InventoryMovement.Record(
            MovementId, OrganizationId, ProductId, null!, InventoryMovementReason.PurchaseReceived, InventorySourceType.PurchaseReceipt, SourceId, ActorId, null, At));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(-1)]
    public void Record_RejectsAnUndefinedReason(int value)
    {
        var exception = Assert.Throws<DomainException>(() => Record((InventoryMovementReason)value, "note"));

        Assert.Equal("inventory_movement.reason_invalid", exception.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void Record_RejectsAnUndefinedSourceType(int value)
    {
        var exception = Assert.Throws<DomainException>(() => Record(sourceType: (InventorySourceType)value));

        Assert.Equal("inventory_movement.source_type_invalid", exception.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Record_RequiresANoteForAManualAdjustment(string? note)
    {
        var exception = Assert.Throws<DomainException>(() => Record(InventoryMovementReason.ManualAdjustment, note, sourceType: InventorySourceType.Adjustment));

        Assert.Equal("inventory_movement.note_required", exception.Code);
    }

    [Fact]
    public void Record_AcceptsAManualAdjustmentWithANote()
    {
        var movement = Record(InventoryMovementReason.ManualAdjustment, "Stock count found 4 fewer", -4m, sourceType: InventorySourceType.Adjustment);

        Assert.Equal("Stock count found 4 fewer", movement.Note);
    }

    [Theory]
    [InlineData(InventoryMovementReason.PurchaseReceived)]
    [InlineData(InventoryMovementReason.SaleCompleted)]
    [InlineData(InventoryMovementReason.Return)]
    [InlineData(InventoryMovementReason.Damage)]
    public void Record_DoesNotRequireANoteForOtherReasons_AndStoresNoneAsNull(InventoryMovementReason reason)
    {
        Assert.Null(Record(reason, null).Note);
        Assert.Null(Record(reason, "   ").Note);
    }

    [Fact]
    public void Record_TrimsTheNote_AndEnforcesItsLength()
    {
        Assert.Equal("trimmed", Record(InventoryMovementReason.Damage, "  trimmed  ").Note);

        var longest = new string('n', InventoryMovement.MaxNoteLength);
        Assert.Equal(longest, Record(InventoryMovementReason.Damage, longest).Note);
        Assert.Equal(
            "inventory_movement.note_too_long",
            Assert.Throws<DomainException>(() => Record(InventoryMovementReason.Damage, new string('n', InventoryMovement.MaxNoteLength + 1))).Code);
    }

    [Fact]
    public void Record_RejectsATimestampThatIsNotUtc()
    {
        var exception = Assert.Throws<DomainException>(() => Record(at: new DateTimeOffset(2026, 4, 2, 10, 0, 0, TimeSpan.FromHours(1))));

        Assert.Equal("inventory_movement.timestamp_not_utc", exception.Code);
    }

    // ---- the sign of the change follows its reason

    [Theory]
    [InlineData(InventoryMovementReason.PurchaseReceived, 5)]
    [InlineData(InventoryMovementReason.SaleCompleted, -5)]
    [InlineData(InventoryMovementReason.ManualAdjustment, 5)]
    [InlineData(InventoryMovementReason.ManualAdjustment, -5)]
    [InlineData(InventoryMovementReason.Return, 5)]
    [InlineData(InventoryMovementReason.Damage, -5)]
    public void Record_AcceptsTheAllowedSignForTheReason(InventoryMovementReason reason, int amount)
    {
        var movement = Record(reason, note: "reason for the change", delta: amount);

        Assert.Equal(reason, movement.Reason);
        Assert.Equal(amount, movement.QuantityDelta.Value);
    }

    [Theory]
    [InlineData(InventoryMovementReason.PurchaseReceived, -5, "inventory_movement.delta_must_be_positive")]
    [InlineData(InventoryMovementReason.Return, -5, "inventory_movement.delta_must_be_positive")]
    [InlineData(InventoryMovementReason.SaleCompleted, 5, "inventory_movement.delta_must_be_negative")]
    [InlineData(InventoryMovementReason.Damage, 5, "inventory_movement.delta_must_be_negative")]
    public void Record_RejectsTheForbiddenSignForTheReason(InventoryMovementReason reason, int amount, string expectedCode)
    {
        var exception = Assert.Throws<DomainException>(() => Record(reason, note: "reason for the change", delta: amount));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public void TheSignRule_AppliesToTheSmallestRepresentableChange()
    {
        Assert.Equal("inventory_movement.delta_must_be_positive", Assert.Throws<DomainException>(() => Record(InventoryMovementReason.PurchaseReceived, delta: -0.0001m)).Code);
        Assert.Equal("inventory_movement.delta_must_be_negative", Assert.Throws<DomainException>(() => Record(InventoryMovementReason.SaleCompleted, delta: 0.0001m)).Code);
        Assert.Equal(0.0001m, Record(InventoryMovementReason.Return, delta: 0.0001m).QuantityDelta.Value);
        Assert.Equal(-0.0001m, Record(InventoryMovementReason.Damage, delta: -0.0001m).QuantityDelta.Value);
    }

    [Fact]
    public void EveryReason_HasADefinedSignRule()
    {
        // Guards against adding a reason without deciding which sign it allows: each reason must either accept
        // exactly one sign or be explicitly unrestricted.
        foreach (var reason in Enum.GetValues<InventoryMovementReason>())
        {
            var acceptsPositive = Record_Accepts(reason, 5m);
            var acceptsNegative = Record_Accepts(reason, -5m);

            Assert.True(acceptsPositive || acceptsNegative, $"{reason} accepts neither sign");
            Assert.Equal(reason == InventoryMovementReason.ManualAdjustment, acceptsPositive && acceptsNegative);
        }
    }

    private static bool Record_Accepts(InventoryMovementReason reason, decimal delta)
    {
        try
        {
            _ = Record(reason, note: "reason for the change", delta: delta);
            return true;
        }
        catch (DomainException)
        {
            return false;
        }
    }

    [Fact]
    public void AMovement_IsAppendOnly_ItHasNoSettersAndNoMutatingMethods()
    {
        var type = typeof(InventoryMovement);

        var publicSetters = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod is { IsPublic: true })
            .Select(property => property.Name)
            .ToArray();
        var publicInstanceMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .ToArray();

        Assert.Empty(publicSetters);
        Assert.Empty(publicInstanceMethods);
    }
}
