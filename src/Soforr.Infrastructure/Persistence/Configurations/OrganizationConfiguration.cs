using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Soforr.Domain.Common;
using Soforr.Domain.Organizations;

namespace Soforr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Organization"/>. The Domain enforces the invariants; the CHECK constraints are a second line of
/// defense so that data written by anything other than the Domain (a script, a manual fix) cannot violate them.
/// </summary>
internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "Organizations",
            table =>
            {
                table.HasCheckConstraint("CK_Organizations_Name_NotBlank", "char_length(btrim(\"Name\")) > 0");
                table.HasCheckConstraint("CK_Organizations_Currency_Format", "\"Currency\" ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("CK_Organizations_TimeZoneId_NotBlank", "char_length(btrim(\"TimeZoneId\")) > 0");
            });

        builder.HasKey(organization => organization.Id);

        // Identifiers are generated in application code (IIdGenerator), never by the database.
        builder.Property(organization => organization.Id).ValueGeneratedNever();

        builder.Property(organization => organization.Name)
            .IsRequired()
            .HasMaxLength(Organization.MaxNameLength);

        // Stored as the three-letter ISO 4217 code. Reading goes back through Currency.Create.
        builder.Property(organization => organization.Currency)
            .IsRequired()
            .HasMaxLength(3)
            .HasConversion(currency => currency.Code, code => Currency.Create(code));

        builder.Property(organization => organization.TimeZoneId)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(organization => organization.CreatedAtUtc).IsRequired();
        builder.Property(organization => organization.UpdatedAtUtc).IsRequired();
    }
}
