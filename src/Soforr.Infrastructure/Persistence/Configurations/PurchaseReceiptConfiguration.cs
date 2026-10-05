using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Purchasing;

namespace Soforr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="PurchaseReceipt"/>. The key is (organization, receipt id): a receipt id is unique within an
/// organization only, so another organization may use the same id independently. The row is inserted in the same
/// transaction as the stock movements, so the key is also the database's guard that one id is one receipt.
/// </summary>
internal sealed class PurchaseReceiptConfiguration : IEntityTypeConfiguration<PurchaseReceipt>
{
    public void Configure(EntityTypeBuilder<PurchaseReceipt> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PurchaseReceipts");

        builder.HasKey(receipt => new { receipt.OrganizationId, receipt.Id });
        builder.Property(receipt => receipt.Id).ValueGeneratedNever();
        builder.Property(receipt => receipt.PurchaseOrderId).IsRequired();
        builder.Property(receipt => receipt.ReceivedByUserId).IsRequired();
        builder.Property(receipt => receipt.ReceivedAtUtc).IsRequired();

        // The order is referenced together with the organization, so a receipt can never point at another organization's order.
        builder.HasOne<PurchaseOrder>()
            .WithMany()
            .HasForeignKey(receipt => new { receipt.PurchaseOrderId, receipt.OrganizationId })
            .HasPrincipalKey(order => new { order.Id, order.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(receipt => receipt.Lines)
            .WithOne()
            .HasForeignKey(line => new { line.OrganizationId, line.ReceiptId })
            .HasPrincipalKey(receipt => new { receipt.OrganizationId, receipt.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(receipt => receipt.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Maps <see cref="PurchaseReceiptLine"/>. There is deliberately no foreign key to an order line: the receipt is a record of what happened and the line is identified by its id.</summary>
internal sealed class PurchaseReceiptLineConfiguration : IEntityTypeConfiguration<PurchaseReceiptLine>
{
    public void Configure(EntityTypeBuilder<PurchaseReceiptLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PurchaseReceiptLines", table => table.HasCheckConstraint("CK_PurchaseReceiptLines_Quantity_Positive", "\"Quantity\" > 0"));

        builder.HasKey(line => new { line.OrganizationId, line.ReceiptId, line.LineId });
        builder.Property(line => line.ProductId).IsRequired();
        builder.Property(line => line.Quantity)
            .IsRequired()
            .HasPrecision(18, 4)
            .HasConversion(quantity => quantity.Value, value => Quantity.Create(value));

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(line => new { line.ProductId, line.OrganizationId })
            .HasPrincipalKey(product => new { product.Id, product.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
