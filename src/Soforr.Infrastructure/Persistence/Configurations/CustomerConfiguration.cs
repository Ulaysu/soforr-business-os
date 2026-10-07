using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Soforr.Domain.Customers;
using Soforr.Domain.Organizations;

namespace Soforr.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="Customer"/>. The Domain enforces the invariants; the CHECK constraints are a second line of defense.</summary>
internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "Customers",
            table =>
            {
                table.HasCheckConstraint("CK_Customers_Name_NotBlank", "char_length(btrim(\"Name\")) > 0");
                table.HasCheckConstraint("CK_Customers_ContactDetails_NotBlank", "\"ContactDetails\" IS NULL OR char_length(btrim(\"ContactDetails\")) > 0");
                table.HasCheckConstraint("CK_Customers_CreditTermsDays_Range", $"\"CreditTermsDays\" IS NULL OR (\"CreditTermsDays\" >= 0 AND \"CreditTermsDays\" <= {Customer.MaxCreditTermsDays})");
            });

        builder.HasKey(customer => customer.Id);
        builder.Property(customer => customer.Id).ValueGeneratedNever();

        // Lets sales reference a customer together with the organization, so the database guarantees a sale and its customer
        // belong to the same organization (CUSTOMER-002).
        builder.HasAlternateKey(customer => new { customer.Id, customer.OrganizationId }).HasName("AK_Customers_Id_OrganizationId");

        builder.Property(customer => customer.OrganizationId).IsRequired();
        builder.Property(customer => customer.Name).IsRequired().HasMaxLength(Customer.MaxNameLength);
        builder.Property(customer => customer.ContactDetails).HasMaxLength(Customer.MaxContactDetailsLength);
        builder.Property(customer => customer.CreditTermsDays);
        builder.Property(customer => customer.IsActive).IsRequired();
        builder.Property(customer => customer.CreatedAtUtc).IsRequired();
        builder.Property(customer => customer.UpdatedAtUtc).IsRequired();

        builder.HasIndex(customer => new { customer.OrganizationId, customer.IsActive, customer.Name });

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(customer => customer.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
