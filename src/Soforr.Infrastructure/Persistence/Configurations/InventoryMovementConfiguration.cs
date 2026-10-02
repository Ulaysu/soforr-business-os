using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Soforr.Domain.Catalog;
using Soforr.Domain.Inventory;

namespace Soforr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="InventoryMovement"/>, the append-only ledger behind every stock level (ADR 0004). The Domain
/// enforces the invariants; the CHECK constraints and the unique idempotency index are the database's second line, so
/// data written by anything else cannot break them.
/// </summary>
internal sealed class InventoryMovementConfiguration : IEntityTypeConfiguration<InventoryMovement>
{
    /// <summary>
    /// The idempotency key of ADR 0004: the same source cannot record the same movement of the same product for the
    /// same reason twice (re-receiving a receipt line, re-completing a sale). A violation has this constraint name.
    /// </summary>
    public const string IdempotencyIndexName = "IX_InventoryMovements_Idempotency";

    public void Configure(EntityTypeBuilder<InventoryMovement> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "InventoryMovements",
            table =>
            {
                table.HasCheckConstraint("CK_InventoryMovements_QuantityDelta_NotZero", "\"QuantityDelta\" <> 0");
                table.HasCheckConstraint("CK_InventoryMovements_Reason_Known", "\"Reason\" IN ('PurchaseReceived', 'SaleCompleted', 'ManualAdjustment', 'Return', 'Damage')");
                table.HasCheckConstraint("CK_InventoryMovements_SourceType_Known", "\"SourceType\" IN ('PurchaseReceipt', 'Sale', 'Adjustment')");

                // The reason fixes the sign of the change (see InventoryMovement); a manual adjustment may be either.
                table.HasCheckConstraint(
                    "CK_InventoryMovements_Reason_MatchesSign",
                    "(\"Reason\" IN ('PurchaseReceived', 'Return') AND \"QuantityDelta\" > 0) " +
                    "OR (\"Reason\" IN ('SaleCompleted', 'Damage') AND \"QuantityDelta\" < 0) " +
                    "OR \"Reason\" = 'ManualAdjustment'");
                table.HasCheckConstraint(
                    "CK_InventoryMovements_Note_RequiredForAdjustment",
                    "\"Reason\" <> 'ManualAdjustment' OR (\"Note\" IS NOT NULL AND char_length(btrim(\"Note\")) > 0)");
                table.HasCheckConstraint("CK_InventoryMovements_Note_NotBlank", "\"Note\" IS NULL OR char_length(btrim(\"Note\")) > 0");
            });

        builder.HasKey(movement => movement.Id);
        builder.Property(movement => movement.Id).ValueGeneratedNever();

        builder.Property(movement => movement.OrganizationId).IsRequired();
        builder.Property(movement => movement.ProductId).IsRequired();

        builder.Property(movement => movement.QuantityDelta)
            .IsRequired()
            .HasPrecision(18, 4)
            .HasConversion(delta => delta.Value, value => QuantityDelta.Create(value));

        builder.Property(movement => movement.Reason).IsRequired().HasMaxLength(30).HasConversion<string>();
        builder.Property(movement => movement.SourceType).IsRequired().HasMaxLength(30).HasConversion<string>();
        builder.Property(movement => movement.SourceId).IsRequired();

        // The actor is the user who caused the movement. There is deliberately no foreign key to the user table: a
        // movement outlives any user and the system actors of later automation are not users (open question).
        builder.Property(movement => movement.ActorId).IsRequired();

        builder.Property(movement => movement.Note).HasMaxLength(InventoryMovement.MaxNoteLength);
        builder.Property(movement => movement.OccurredAtUtc).IsRequired();

        builder.HasIndex(movement => new { movement.OrganizationId, movement.SourceType, movement.SourceId, movement.ProductId, movement.Reason })
            .IsUnique()
            .HasDatabaseName(IdempotencyIndexName);

        // The history of one product, oldest first.
        builder.HasIndex(movement => new { movement.OrganizationId, movement.ProductId, movement.OccurredAtUtc });

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(movement => new { movement.ProductId, movement.OrganizationId })
            .HasPrincipalKey(product => new { product.Id, product.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
