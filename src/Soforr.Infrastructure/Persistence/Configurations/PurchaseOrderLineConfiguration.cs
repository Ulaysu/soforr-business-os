using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Purchasing;

namespace Soforr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="PurchaseOrderLine"/>. The product is referenced together with the organization (a composite key), so a
/// line can never point at another organization's product; the snapshots are plain columns that are never updated from the
/// product. A product appears at most once on an order, enforced by a unique index.
/// </summary>
internal sealed class PurchaseOrderLineConfiguration : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "PurchaseOrderLines",
            table =>
            {
                table.HasCheckConstraint("CK_PurchaseOrderLines_OrderedQuantity_Positive", "\"OrderedQuantity\" > 0");
                table.HasCheckConstraint("CK_PurchaseOrderLines_ReceivedQuantity_WithinOrdered", "\"ReceivedQuantity\" >= 0 AND \"ReceivedQuantity\" <= \"OrderedQuantity\"");
                table.HasCheckConstraint("CK_PurchaseOrderLines_UnitCost_NotNegative", "\"UnitCost\" >= 0");
                table.HasCheckConstraint("CK_PurchaseOrderLines_UnitCostCurrency_Format", "\"UnitCostCurrency\" ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("CK_PurchaseOrderLines_Snapshots_NotBlank", "char_length(btrim(\"SkuSnapshot\")) > 0 AND char_length(btrim(\"NameSnapshot\")) > 0 AND char_length(btrim(\"UnitOfMeasureSnapshot\")) > 0");
            });

        builder.HasKey(line => line.Id);
        builder.Property(line => line.Id).ValueGeneratedNever();

        builder.Property(line => line.OrganizationId).IsRequired();
        builder.Property(line => line.PurchaseOrderId).IsRequired();
        builder.Property(line => line.ProductId).IsRequired();

        builder.Property(line => line.SkuSnapshot).IsRequired().HasMaxLength(PurchaseOrderLine.MaxSkuLength);
        builder.Property(line => line.NameSnapshot).IsRequired().HasMaxLength(PurchaseOrderLine.MaxNameLength);
        builder.Property(line => line.UnitOfMeasureSnapshot).IsRequired().HasMaxLength(PurchaseOrderLine.MaxUnitOfMeasureLength);

        builder.Property(line => line.OrderedQuantity)
            .IsRequired()
            .HasPrecision(18, 4)
            .HasConversion(quantity => quantity.Value, value => Quantity.Create(value));
        builder.Property(line => line.ReceivedQuantity)
            .IsRequired()
            .HasPrecision(18, 4)
            .HasConversion(quantity => quantity.Value, value => Quantity.Create(value));

        builder.OwnsOne(
            line => line.UnitCost,
            cost =>
            {
                cost.Property(money => money.Amount).HasColumnName("UnitCost").IsRequired();
                cost.Property(money => money.Currency).HasColumnName("UnitCostCurrency").IsRequired().HasMaxLength(3)
                    .HasConversion(currency => currency.Code, code => Currency.Create(code));
            });
        builder.Navigation(line => line.UnitCost).IsRequired();

        // A product appears at most once on an order.
        builder.HasIndex(line => new { line.PurchaseOrderId, line.ProductId }).IsUnique();

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(line => new { line.ProductId, line.OrganizationId })
            .HasPrincipalKey(product => new { product.Id, product.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
