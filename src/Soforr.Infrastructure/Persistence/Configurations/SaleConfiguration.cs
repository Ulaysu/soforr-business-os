using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Customers;
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

                // A sale made on account has a customer and a due date; a walk-in sale has neither (it is paid in full).
                table.HasCheckConstraint("CK_Sales_Customer_MatchesDueDate", "(\"CustomerId\" IS NULL) = (\"DueAtUtc\" IS NULL)");
                table.HasCheckConstraint("CK_Sales_DueAt_NotBeforeSale", "\"DueAtUtc\" IS NULL OR \"DueAtUtc\" >= \"CreatedAtUtc\"");
                table.HasCheckConstraint("CK_Sales_PaymentRevision_NotNegative", "\"PaymentRevision\" >= 0");
            });

        // The key is (organization, sale id): a sale id may be chosen by the client (to make a retry safe), so it is unique
        // within an organization only. Another organization may use the same id independently, and one organization can
        // never collide with, or learn about, another's. The lines reference the sale through this key.
        builder.HasKey(sale => new { sale.OrganizationId, sale.Id });
        builder.Property(sale => sale.Id).ValueGeneratedNever();

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

        builder.Property(sale => sale.CustomerId);
        builder.Property(sale => sale.DueAtUtc);
        builder.Property(sale => sale.PaymentRevision).IsRequired().HasDefaultValue(0);

        // The customer is referenced together with the organization, so a sale can never be on account to another organization's customer.
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(sale => new { sale.CustomerId, sale.OrganizationId })
            .HasPrincipalKey(customer => new { customer.Id, customer.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(sale => sale.Payments)
            .WithOne()
            .HasForeignKey(payment => new { payment.OrganizationId, payment.SaleId })
            .HasPrincipalKey(sale => new { sale.OrganizationId, sale.Id })
            .OnDelete(DeleteBehavior.Restrict); // payments are never deleted
        builder.Navigation(sale => sale.Payments).UsePropertyAccessMode(PropertyAccessMode.Field);

        // The token changes on every update of the row; a void updates it.
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasMany(sale => sale.Lines)
            .WithOne()
            .HasForeignKey(line => new { line.OrganizationId, line.SaleId })
            .HasPrincipalKey(sale => new { sale.OrganizationId, sale.Id })
            .OnDelete(DeleteBehavior.Restrict); // a sale and its lines are never deleted
        builder.Navigation(sale => sale.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(sale => new { sale.OrganizationId, sale.CreatedAtUtc });
        builder.HasIndex(sale => new { sale.OrganizationId, sale.Status, sale.CreatedAtUtc });
        builder.HasIndex(sale => new { sale.OrganizationId, sale.CustomerId, sale.DueAtUtc });
    }
}

/// <summary>
/// Maps <see cref="SalePayment"/>. The key is (organization, payment id): a payment id may be chosen by the client so a retry
/// is safe, so it is unique within an organization only. Records are append-only. A reversal points at the payment it takes
/// back through a foreign key that includes the sale, so a reversal can only reverse a payment of its own sale; and a payment
/// can be reversed at most once, by a unique index.
/// </summary>
internal sealed class SalePaymentConfiguration : IEntityTypeConfiguration<SalePayment>
{
    public void Configure(EntityTypeBuilder<SalePayment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "SalePayments",
            table =>
            {
                table.HasCheckConstraint("CK_SalePayments_Kind_Known", "\"Kind\" IN ('Payment', 'Reversal')");
                table.HasCheckConstraint("CK_SalePayments_Amount_Positive", "\"Amount\" > 0");
                table.HasCheckConstraint("CK_SalePayments_AmountCurrency_Format", "\"AmountCurrency\" ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("CK_SalePayments_MethodLabel_NotBlank", "\"MethodLabel\" IS NULL OR char_length(btrim(\"MethodLabel\")) > 0");
                table.HasCheckConstraint("CK_SalePayments_PaidAt_NotAfterRecorded", "\"PaidAtUtc\" <= \"RecordedAtUtc\"");

                // A reversal says which payment it takes back and why; a payment does neither.
                table.HasCheckConstraint(
                    "CK_SalePayments_Reversal_MatchesKind",
                    "(\"Kind\" = 'Reversal') = (\"ReversesPaymentId\" IS NOT NULL) AND (\"Kind\" = 'Reversal') = (\"ReversalReason\" IS NOT NULL)");
                table.HasCheckConstraint("CK_SalePayments_ReversalReason_NotBlank", "\"ReversalReason\" IS NULL OR char_length(btrim(\"ReversalReason\")) > 0");
            });

        builder.HasKey(payment => new { payment.OrganizationId, payment.Id });
        builder.Property(payment => payment.Id).ValueGeneratedNever();

        // Lets a reversal reference the payment it reverses together with the sale.
        builder.HasAlternateKey(payment => new { payment.OrganizationId, payment.SaleId, payment.Id }).HasName("AK_SalePayments_OrganizationId_SaleId_Id");

        builder.Property(payment => payment.SaleId).IsRequired();
        builder.Property(payment => payment.Kind).IsRequired().HasMaxLength(20).HasConversion<string>();
        builder.Property(payment => payment.PaidAtUtc).IsRequired();
        builder.Property(payment => payment.MethodLabel).HasMaxLength(SalePayment.MaxMethodLabelLength);
        builder.Property(payment => payment.ReversesPaymentId);
        builder.Property(payment => payment.ReversalReason).HasMaxLength(SalePayment.MaxReversalReasonLength);

        // There is deliberately no foreign key to the user table: a record outlives the people who made it (as movements do).
        builder.Property(payment => payment.RecordedByUserId).IsRequired();
        builder.Property(payment => payment.RecordedAtUtc).IsRequired();

        builder.OwnsOne(
            payment => payment.Amount,
            amount =>
            {
                amount.Property(money => money.Amount).HasColumnName("Amount").IsRequired();
                amount.Property(money => money.Currency).HasColumnName("AmountCurrency").IsRequired().HasMaxLength(3)
                    .HasConversion(currency => currency.Code, code => Currency.Create(code));
            });
        builder.Navigation(payment => payment.Amount).IsRequired();

        builder.HasOne<SalePayment>()
            .WithMany()
            .HasForeignKey(payment => new { payment.OrganizationId, payment.SaleId, payment.ReversesPaymentId })
            .HasPrincipalKey(payment => new { payment.OrganizationId, payment.SaleId, payment.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // A payment can be reversed at most once. (Several rows with no reversed payment are allowed: NULLs are distinct.)
        builder.HasIndex(payment => new { payment.OrganizationId, payment.ReversesPaymentId }).IsUnique();
        builder.HasIndex(payment => new { payment.OrganizationId, payment.SaleId, payment.RecordedAtUtc });
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
