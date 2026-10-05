using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Sales;

namespace Soforr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Sale"/>. It carries the optimistic concurrency token of ADR 0004 (PostgreSQL's <c>xmin</c>, a shadow
/// property: it is a system column and the migration does not create it), so two people voiding the same sale at once
/// cannot both save. The CHECK constraints repeat the void rules as a second line of defense behind the Domain.
/// </summary>
internal sealed class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    public void Configure(EntityTypeBuilder<Sale> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "Sales",
            table =>
            {
                table.HasCheckConstraint("CK_Sales_Status_Known", "\"Status\" IN ('Completed', 'Voided')");
                table.HasCheckConstraint("CK_Sales_Currency_Format", "\"Currency\" ~ '^[A-Z]{3}$'");

                // A sale is voided exactly when it has a void time, a voiding user and a reason.
                table.HasCheckConstraint(
                    "CK_Sales_Void_MatchesStatus",
                    "(\"Status\" = 'Voided') = (\"VoidedAtUtc\" IS NOT NULL) AND (\"Status\" = 'Voided') = (\"VoidedByUserId\" IS NOT NULL) AND (\"Status\" = 'Voided') = (\"VoidReason\" IS NOT NULL)");
                table.HasCheckConstraint("CK_Sales_VoidReason_NotBlank", "\"VoidReason\" IS NULL OR char_length(btrim(\"VoidReason\")) > 0");
                table.HasCheckConstraint("CK_Sales_VoidedAt_NotBeforeSale", "\"VoidedAtUtc\" IS NULL OR \"VoidedAtUtc\" >= \"CreatedAtUtc\"");
            });

        builder.HasKey(sale => sale.Id);
        builder.Property(sale => sale.Id).ValueGeneratedNever();

        // Lets the lines reference the sale together with the organization.
        builder.HasAlternateKey(sale => new { sale.Id, sale.OrganizationId }).HasName("AK_Sales_Id_OrganizationId");

        builder.Property(sale => sale.OrganizationId).IsRequired();

        builder.Property(sale => sale.Currency)
            .IsRequired()
            .HasMaxLength(3)
            .HasConversion(currency => currency.Code, code => Currency.Create(code));

        builder.Property(sale => sale.Status).IsRequired().HasMaxLength(20).HasConversion<string>();

        // There is deliberately no foreign key to the user table: a sale outlives the people who made it (as movements do).
        builder.Property(sale => sale.CreatedByUserId).IsRequired();
        builder.Property(sale => sale.CreatedAtUtc).IsRequired();
        builder.Property(sale => sale.VoidedAtUtc);
        builder.Property(sale => sale.VoidedByUserId);
        builder.Property(sale => sale.VoidReason).HasMaxLength(Sale.MaxVoidReasonLength);

        // The token changes on every update of the row; a void updates it.
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasMany(sale => sale.Lines)
            .WithOne()
            .HasForeignKey(line => new { line.SaleId, line.OrganizationId })
            .HasPrincipalKey(sale => new { sale.Id, sale.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict); // a sale and its lines are never deleted
        builder.Navigation(sale => sale.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(sale => new { sale.OrganizationId, sale.CreatedAtUtc });
        builder.HasIndex(sale => new { sale.OrganizationId, sale.Status, sale.CreatedAtUtc });
    }
}

/// <summary>
/// Maps <see cref="SaleLine"/>. The product is referenced together with the organization (a composite key), so a line can
/// never point at another organization's product; the snapshots are plain columns that are never updated from the
/// product. A product appears at most once on a sale, enforced by a unique index.
/// </summary>
internal sealed class SaleLineConfiguration : IEntityTypeConfiguration<SaleLine>
{
    public void Configure(EntityTypeBuilder<SaleLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "SaleLines",
            table =>
            {
                table.HasCheckConstraint("CK_SaleLines_Quantity_Positive", "\"Quantity\" > 0");
                table.HasCheckConstraint("CK_SaleLines_UnitPrice_NotNegative", "\"UnitPrice\" >= 0");
                table.HasCheckConstraint("CK_SaleLines_CatalogUnitPrice_NotNegative", "\"CatalogUnitPrice\" >= 0");
                table.HasCheckConstraint("CK_SaleLines_CostPrice_NotNegative", "\"CostPrice\" >= 0");
                table.HasCheckConstraint("CK_SaleLines_Currencies_Format", "\"UnitPriceCurrency\" ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("CK_SaleLines_Currencies_Match", "\"UnitPriceCurrency\" = \"CatalogUnitPriceCurrency\" AND \"UnitPriceCurrency\" = \"CostPriceCurrency\"");
                table.HasCheckConstraint("CK_SaleLines_Snapshots_NotBlank", "char_length(btrim(\"SkuSnapshot\")) > 0 AND char_length(btrim(\"NameSnapshot\")) > 0 AND char_length(btrim(\"UnitOfMeasureSnapshot\")) > 0");

                // A price that differs from the catalog price has a reason, and only such a price does.
                table.HasCheckConstraint(
                    "CK_SaleLines_OverrideReason_MatchesPrice",
                    "(\"UnitPrice\" <> \"CatalogUnitPrice\") = (\"PriceOverrideReason\" IS NOT NULL) AND (\"PriceOverrideReason\" IS NULL OR char_length(btrim(\"PriceOverrideReason\")) > 0)");
            });

        builder.HasKey(line => line.Id);
        builder.Property(line => line.Id).ValueGeneratedNever();

        builder.Property(line => line.OrganizationId).IsRequired();
        builder.Property(line => line.SaleId).IsRequired();
        builder.Property(line => line.ProductId).IsRequired();

        builder.Property(line => line.SkuSnapshot).IsRequired().HasMaxLength(SaleLine.MaxSkuLength);
        builder.Property(line => line.NameSnapshot).IsRequired().HasMaxLength(SaleLine.MaxNameLength);
        builder.Property(line => line.UnitOfMeasureSnapshot).IsRequired().HasMaxLength(SaleLine.MaxUnitOfMeasureLength);
        builder.Property(line => line.PriceOverrideReason).HasMaxLength(SaleLine.MaxOverrideReasonLength);

        builder.Property(line => line.Quantity)
            .IsRequired()
            .HasPrecision(18, 4)
            .HasConversion(quantity => quantity.Value, value => Quantity.Create(value));

        MapMoney(builder, line => line.CatalogUnitPrice, "CatalogUnitPrice");
        MapMoney(builder, line => line.UnitPrice, "UnitPrice");
        MapMoney(builder, line => line.CostPrice, "CostPrice");

        // A product appears at most once on a sale.
        builder.HasIndex(line => new { line.SaleId, line.ProductId }).IsUnique();

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(line => new { line.ProductId, line.OrganizationId })
            .HasPrincipalKey(product => new { product.Id, product.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void MapMoney(EntityTypeBuilder<SaleLine> builder, System.Linq.Expressions.Expression<Func<SaleLine, Money?>> navigation, string column)
    {
        builder.OwnsOne(
            navigation,
            money =>
            {
                money.Property(value => value.Amount).HasColumnName(column).IsRequired();
                money.Property(value => value.Currency).HasColumnName(column + "Currency").IsRequired().HasMaxLength(3)
                    .HasConversion(currency => currency.Code, code => Currency.Create(code));
            });
        builder.Navigation(navigation).IsRequired();
    }
}
