using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Inventory;

namespace Soforr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="StockLevel"/>: one row per product of an organization. It carries the optimistic concurrency token
/// of ADR 0004 (PostgreSQL's <c>xmin</c>, a shadow property so the Domain stays free of persistence concerns) and a
/// CHECK that stock is never negative, as a backstop behind the Domain rule.
/// </summary>
internal sealed class StockLevelConfiguration : IEntityTypeConfiguration<StockLevel>
{
    public void Configure(EntityTypeBuilder<StockLevel> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("StockLevels", table => table.HasCheckConstraint("CK_StockLevels_Quantity_NotNegative", "\"Quantity\" >= 0"));

        builder.HasKey(level => new { level.OrganizationId, level.ProductId });

        builder.Property(level => level.Quantity)
            .IsRequired()
            .HasPrecision(18, 4)
            .HasConversion(quantity => quantity.Value, value => Quantity.Create(value));

        builder.Property(level => level.UpdatedAtUtc).IsRequired();

        // The token changes on every update of the row, so two writers that read the same level cannot both save (ADR 0004).
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        // The product must belong to the same organization as the level (a composite key on the product).
        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(level => new { level.ProductId, level.OrganizationId })
            .HasPrincipalKey(product => new { product.Id, product.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
