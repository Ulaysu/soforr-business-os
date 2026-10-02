using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Organizations;

namespace Soforr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Product"/>. The Domain enforces the invariants; the CHECK constraints are a second line of
/// defense so data written by anything else cannot violate them. Uniqueness of the SKU is a rule across products, so
/// only the database can enforce it (ADR 0006): see <see cref="SkuKeyColumn"/>.
/// </summary>
internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    /// <summary>The name of the unique index that enforces the SKU rule. A save that violates it fails with this constraint name (SQLSTATE 23505).</summary>
    public const string UniqueSkuIndexName = "IX_Products_OrganizationId_SkuKey";

    /// <summary>
    /// The case-insensitive comparison key of the SKU: PostgreSQL <c>lower("Sku")</c>, kept by the database as a
    /// stored generated column, so the stored SKU keeps the casing the user entered (ADR 0006) and no application code
    /// can forget to normalize. Handlers that look up a SKU must compare with the same function
    /// (<c>sku.ToLower()</c> in a query translates to it).
    /// </summary>
    public const string SkuKeyColumn = Application.Catalog.ProductSkuKey.PropertyName;

    public void Configure(EntityTypeBuilder<Product> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "Products",
            table =>
            {
                table.HasCheckConstraint("CK_Products_Sku_NotBlank", "char_length(btrim(\"Sku\")) > 0");
                table.HasCheckConstraint("CK_Products_Name_NotBlank", "char_length(btrim(\"Name\")) > 0");
                table.HasCheckConstraint("CK_Products_UnitOfMeasure_NotBlank", "char_length(btrim(\"UnitOfMeasure\")) > 0");
                table.HasCheckConstraint("CK_Products_SellingPrice_NotNegative", "\"SellingPrice\" >= 0");
                table.HasCheckConstraint("CK_Products_CostPrice_NotNegative", "\"CostPrice\" >= 0");
                table.HasCheckConstraint("CK_Products_PriceCurrency_Format", "\"SellingPriceCurrency\" ~ '^[A-Z]{3}$' AND \"CostPriceCurrency\" ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("CK_Products_PriceCurrency_Same", "\"SellingPriceCurrency\" = \"CostPriceCurrency\"");
                table.HasCheckConstraint("CK_Products_ReorderPoint_NotNegative", "\"ReorderPoint\" IS NULL OR \"ReorderPoint\" >= 0");
                table.HasCheckConstraint("CK_Products_ReorderLeadTimeDays_NotNegative", "\"ReorderLeadTimeDays\" IS NULL OR \"ReorderLeadTimeDays\" >= 0");
            });

        builder.HasKey(product => product.Id);

        // Lets the stock level and the movements of a product reference it together with the organization, so the
        // database itself guarantees they belong to the same organization as the product (ADR 0004).
        builder.HasAlternateKey(product => new { product.Id, product.OrganizationId }).HasName("AK_Products_Id_OrganizationId");

        // Identifiers are generated in application code (IIdGenerator), never by the database.
        builder.Property(product => product.Id).ValueGeneratedNever();

        builder.Property(product => product.OrganizationId).IsRequired();

        builder.Property(product => product.Sku).IsRequired().HasMaxLength(Product.MaxSkuLength);
        builder.Property(product => product.Name).IsRequired().HasMaxLength(Product.MaxNameLength);
        builder.Property(product => product.UnitOfMeasure).IsRequired().HasMaxLength(Product.MaxUnitOfMeasureLength);

        builder.OwnsOne(
            product => product.SellingPrice,
            price =>
            {
                price.Property(money => money.Amount).HasColumnName("SellingPrice").IsRequired();
                price.Property(money => money.Currency).HasColumnName("SellingPriceCurrency").IsRequired().HasMaxLength(3)
                    .HasConversion(currency => currency.Code, code => Currency.Create(code));
            });
        builder.Navigation(product => product.SellingPrice).IsRequired();

        builder.OwnsOne(
            product => product.CostPrice,
            price =>
            {
                price.Property(money => money.Amount).HasColumnName("CostPrice").IsRequired();
                price.Property(money => money.Currency).HasColumnName("CostPriceCurrency").IsRequired().HasMaxLength(3)
                    .HasConversion(currency => currency.Code, code => Currency.Create(code));
            });
        builder.Navigation(product => product.CostPrice).IsRequired();

        // Optional: both columns are null when no reorder settings are configured.
        builder.OwnsOne(
            product => product.ReorderSettings,
            settings =>
            {
                settings.Property(reorder => reorder.ReorderPoint).HasColumnName("ReorderPoint").HasPrecision(18, 4)
                    .HasConversion(quantity => quantity.Value, value => Quantity.Create(value));
                settings.Property(reorder => reorder.LeadTimeDays).HasColumnName("ReorderLeadTimeDays");
            });

        builder.Property(product => product.IsActive).IsRequired();
        builder.Property(product => product.CreatedAtUtc).IsRequired();
        builder.Property(product => product.UpdatedAtUtc).IsRequired();

        // The SKU comparison key lives only in the database (see SkuKeyColumn); the Domain has no copy of it.
        builder.Property<string>(SkuKeyColumn)
            .HasMaxLength(Product.MaxSkuLength)
            .HasComputedColumnSql("lower(\"Sku\")", stored: true);

        builder.HasIndex(nameof(Product.OrganizationId), SkuKeyColumn)
            .IsUnique()
            .HasDatabaseName(UniqueSkuIndexName);

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(product => product.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
