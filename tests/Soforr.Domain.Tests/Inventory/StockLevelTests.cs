using Soforr.Domain.Common;
using Soforr.Domain.Inventory;

namespace Soforr.Domain.Tests.Inventory;

public class StockLevelTests
{
    private static readonly Guid OrganizationId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c1a10");
    private static readonly Guid ProductId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c1a11");
    private static readonly Guid ActorId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c1a40");
    private static readonly DateTimeOffset OpenedAt = new(2026, 4, 1, 8, 0, 0, TimeSpan.Zero);

    private static StockLevel NewLevel() => StockLevel.Open(OrganizationId, ProductId, OpenedAt);

    private static InventoryMovement Move(decimal delta, int minutesAfterOpening = 1, Guid? organizationId = null, Guid? productId = null) =>
        InventoryMovement.Record(
            Guid.CreateVersion7(),
            organizationId ?? OrganizationId,
            productId ?? ProductId,
            QuantityDelta.Create(delta),
            delta > 0 ? InventoryMovementReason.PurchaseReceived : InventoryMovementReason.SaleCompleted,
            delta > 0 ? InventorySourceType.PurchaseReceipt : InventorySourceType.Sale,
            Guid.CreateVersion7(),
            ActorId,
            null,
            OpenedAt.AddMinutes(minutesAfterOpening));

    // ---- opening

    [Fact]
    public void Open_StartsWithNoStock()
    {
        var level = NewLevel();

        Assert.Equal(OrganizationId, level.OrganizationId);
        Assert.Equal(ProductId, level.ProductId);
        Assert.Equal(Quantity.Zero, level.Quantity);
        Assert.Equal(OpenedAt, level.UpdatedAtUtc);
    }

    [Fact]
    public void Open_RequiresAnOrganizationAndAProduct_AndAUtcTimestamp()
    {
        Assert.Equal("stock_level.organization_required", Assert.Throws<DomainException>(() => StockLevel.Open(Guid.Empty, ProductId, OpenedAt)).Code);
        Assert.Equal("stock_level.product_required", Assert.Throws<DomainException>(() => StockLevel.Open(OrganizationId, Guid.Empty, OpenedAt)).Code);
        Assert.Equal(
            "stock_level.timestamp_not_utc",
            Assert.Throws<DomainException>(() => StockLevel.Open(OrganizationId, ProductId, new DateTimeOffset(2026, 4, 1, 8, 0, 0, TimeSpan.FromHours(1)))).Code);
    }

    // ---- applying movements

    [Fact]
    public void Apply_AddsAndRemovesStock_AndTracksTheLastMovementTime()
    {
        var level = NewLevel();

        level.Apply(Move(50m, minutesAfterOpening: 5));
        Assert.Equal(Quantity.Create(50m), level.Quantity);
        Assert.Equal(OpenedAt.AddMinutes(5), level.UpdatedAtUtc);

        level.Apply(Move(-12.5m, minutesAfterOpening: 9));
        Assert.Equal(Quantity.Create(37.5m), level.Quantity);
        Assert.Equal(OpenedAt.AddMinutes(9), level.UpdatedAtUtc);
    }

    [Fact]
    public void Apply_AllowsStockToReachExactlyZero()
    {
        var level = NewLevel();
        level.Apply(Move(10m));

        level.Apply(Move(-10m));

        Assert.Equal(Quantity.Zero, level.Quantity);
    }

    [Fact]
    public void Apply_RejectsAMovementThatWouldMakeStockNegative_AndChangesNothing()
    {
        var level = NewLevel();
        level.Apply(Move(10m, minutesAfterOpening: 3));

        var exception = Assert.Throws<DomainException>(() => level.Apply(Move(-10.0001m, minutesAfterOpening: 8)));

        Assert.Equal("stock_level.insufficient_stock", exception.Code);
        Assert.Equal(Quantity.Create(10m), level.Quantity);
        Assert.Equal(OpenedAt.AddMinutes(3), level.UpdatedAtUtc);
    }

    [Fact]
    public void Apply_RejectsRemovingStockFromAnEmptyLevel()
    {
        var level = NewLevel();

        var exception = Assert.Throws<DomainException>(() => level.Apply(Move(-0.0001m)));

        Assert.Equal("stock_level.insufficient_stock", exception.Code);
        Assert.Equal(Quantity.Zero, level.Quantity);
    }

    [Fact]
    public void Apply_RejectsAMovementForAnotherProduct_AndChangesNothing()
    {
        var level = NewLevel();

        var exception = Assert.Throws<DomainException>(() => level.Apply(Move(5m, productId: Guid.CreateVersion7())));

        Assert.Equal("stock_level.movement_mismatch", exception.Code);
        Assert.Equal(Quantity.Zero, level.Quantity);
        Assert.Equal(OpenedAt, level.UpdatedAtUtc);
    }

    [Fact]
    public void Apply_RejectsAMovementFromAnotherOrganization_AndChangesNothing()
    {
        var level = NewLevel();

        var exception = Assert.Throws<DomainException>(() => level.Apply(Move(5m, organizationId: Guid.CreateVersion7())));

        Assert.Equal("stock_level.movement_mismatch", exception.Code);
        Assert.Equal(Quantity.Zero, level.Quantity);
    }

    [Fact]
    public void Apply_RejectsAMovementThatWouldOverflowTheQuantity_AndChangesNothing()
    {
        var level = NewLevel();
        level.Apply(Move(Quantity.MaxValue, minutesAfterOpening: 2));

        var exception = Assert.Throws<DomainException>(() => level.Apply(Move(0.0001m, minutesAfterOpening: 6)));

        Assert.Equal("quantity.too_large", exception.Code);
        Assert.Equal(Quantity.Create(Quantity.MaxValue), level.Quantity);
        Assert.Equal(OpenedAt.AddMinutes(2), level.UpdatedAtUtc);
    }

    [Fact]
    public void Apply_RequiresAMovement()
    {
        Assert.Throws<ArgumentNullException>(() => NewLevel().Apply(null!));
    }

    // ---- the reconciliation invariant: stock level = sum of applied movements

    [Fact]
    public void TheQuantity_AlwaysEqualsTheSumOfTheAppliedMovements()
    {
        var level = NewLevel();
        var applied = new List<decimal>();
        decimal[] attempts = [100m, -30m, -80m, 25.5m, -95.5m, -0.0001m, 0.0001m, -0.0001m, 7m, -7m, 12.3456m];

        foreach (var delta in attempts)
        {
            try
            {
                level.Apply(Move(delta));
                applied.Add(delta);
            }
            catch (DomainException exception) when (exception.Code == "stock_level.insufficient_stock")
            {
                // A rejected movement is not part of the history, so it must not change the quantity.
            }

            Assert.Equal(applied.Sum(), level.Quantity.Value);
        }

        Assert.Contains(-80m, attempts);
        Assert.DoesNotContain(-80m, applied);
        Assert.Equal(9, applied.Count);
    }
}
