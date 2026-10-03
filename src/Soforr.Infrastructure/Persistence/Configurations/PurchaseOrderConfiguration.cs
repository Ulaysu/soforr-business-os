using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Soforr.Domain.Common;
using Soforr.Domain.Purchasing;

namespace Soforr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="PurchaseOrder"/>. It carries the optimistic concurrency token of ADR 0004 (PostgreSQL's <c>xmin</c>, a
/// shadow property: it is a system column and the migration does not create it), so two people receiving or changing the
/// same order at once cannot both save. The CHECK constraints repeat the state machine's consistency rules as a second
/// line of defense behind the Domain.
/// </summary>
internal sealed class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "PurchaseOrders",
            table =>
            {
                table.HasCheckConstraint("CK_PurchaseOrders_Status_Known", "\"Status\" IN ('Draft', 'Placed', 'PartiallyReceived', 'Received', 'Cancelled')");
                table.HasCheckConstraint("CK_PurchaseOrders_Currency_Format", "\"Currency\" ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("CK_PurchaseOrders_Reference_NotBlank", "\"Reference\" IS NULL OR char_length(btrim(\"Reference\")) > 0");

                // A draft has not been placed; every order that is, or was, placed has a placed time.
                table.HasCheckConstraint(
                    "CK_PurchaseOrders_PlacedAt_MatchesStatus",
                    "(\"Status\" = 'Draft' AND \"PlacedAtUtc\" IS NULL) OR (\"Status\" IN ('Placed', 'PartiallyReceived', 'Received') AND \"PlacedAtUtc\" IS NOT NULL) OR \"Status\" = 'Cancelled'");

                // An order is closed exactly when it is received in full or cancelled.
                table.HasCheckConstraint("CK_PurchaseOrders_ClosedAt_MatchesStatus", "(\"Status\" IN ('Received', 'Cancelled')) = (\"ClosedAtUtc\" IS NOT NULL)");
            });

        builder.HasKey(order => order.Id);
        builder.Property(order => order.Id).ValueGeneratedNever();

        // Lets the lines reference the order together with the organization.
        builder.HasAlternateKey(order => new { order.Id, order.OrganizationId }).HasName("AK_PurchaseOrders_Id_OrganizationId");

        builder.Property(order => order.OrganizationId).IsRequired();
        builder.Property(order => order.SupplierId).IsRequired();

        builder.Property(order => order.Currency)
            .IsRequired()
            .HasMaxLength(3)
            .HasConversion(currency => currency.Code, code => Currency.Create(code));

        builder.Property(order => order.Reference).HasMaxLength(PurchaseOrder.MaxReferenceLength);
        builder.Property(order => order.Status).IsRequired().HasMaxLength(20).HasConversion<string>();
        builder.Property(order => order.CreatedAtUtc).IsRequired();
        builder.Property(order => order.UpdatedAtUtc).IsRequired();
        builder.Property(order => order.PlacedAtUtc);
        builder.Property(order => order.ClosedAtUtc);

        // The token changes on every update of the row. Every change to an order (and to its lines) updates the order's
        // UpdatedAtUtc, so the row always changes and the token always moves.
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(order => new { order.SupplierId, order.OrganizationId })
            .HasPrincipalKey(supplier => new { supplier.Id, supplier.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(order => order.Lines)
            .WithOne()
            .HasForeignKey(line => new { line.PurchaseOrderId, line.OrganizationId })
            .HasPrincipalKey(order => new { order.Id, order.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade); // lines belong to the order: removing one from a draft deletes its row
        builder.Navigation(order => order.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(order => new { order.OrganizationId, order.Status, order.CreatedAtUtc });
        builder.HasIndex(order => new { order.OrganizationId, order.SupplierId });
    }
}
