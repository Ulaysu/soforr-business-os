using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Soforr.Domain.Organizations;
using Soforr.Domain.Purchasing;

namespace Soforr.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="Supplier"/>. The Domain enforces the invariants; the CHECK constraints are a second line of defense.</summary>
internal sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "Suppliers",
            table =>
            {
                table.HasCheckConstraint("CK_Suppliers_Name_NotBlank", "char_length(btrim(\"Name\")) > 0");
                table.HasCheckConstraint("CK_Suppliers_ContactDetails_NotBlank", "\"ContactDetails\" IS NULL OR char_length(btrim(\"ContactDetails\")) > 0");
            });

        builder.HasKey(supplier => supplier.Id);
        builder.Property(supplier => supplier.Id).ValueGeneratedNever();

        // Lets purchase orders reference a supplier together with the organization, so the database guarantees an order
        // and its supplier belong to the same organization.
        builder.HasAlternateKey(supplier => new { supplier.Id, supplier.OrganizationId }).HasName("AK_Suppliers_Id_OrganizationId");

        builder.Property(supplier => supplier.OrganizationId).IsRequired();
        builder.Property(supplier => supplier.Name).IsRequired().HasMaxLength(Supplier.MaxNameLength);
        builder.Property(supplier => supplier.ContactDetails).HasMaxLength(Supplier.MaxContactDetailsLength);
        builder.Property(supplier => supplier.IsActive).IsRequired();
        builder.Property(supplier => supplier.CreatedAtUtc).IsRequired();
        builder.Property(supplier => supplier.UpdatedAtUtc).IsRequired();

        builder.HasIndex(supplier => new { supplier.OrganizationId, supplier.Name });

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(supplier => supplier.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
