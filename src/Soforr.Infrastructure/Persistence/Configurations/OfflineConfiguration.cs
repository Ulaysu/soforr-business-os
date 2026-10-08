using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Soforr.Domain.Offline;

namespace Soforr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="OfflineGrant"/> (ADR 0008). The key is (organization, id). There is deliberately no foreign key to the user table
/// (a grant outlives the people it was issued to, as sales do) and sales keep the grant id without a foreign key (grants are pruned).
/// A user has at most one active grant, enforced by a partial unique index, so two simultaneous issuances cannot both stay active.
/// A grant is changed only to supersede or revoke it, so it carries the optimistic concurrency token of ADR 0004.
/// </summary>
internal sealed class OfflineGrantConfiguration : IEntityTypeConfiguration<OfflineGrant>
{
    public void Configure(EntityTypeBuilder<OfflineGrant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "OfflineGrants",
            table =>
            {
                table.HasCheckConstraint("CK_OfflineGrants_Expiry_AfterIssue", "\"AbsoluteExpiresAtUtc\" > \"IssuedAtUtc\"");
                table.HasCheckConstraint("CK_OfflineGrants_Superseded_NotBeforeIssue", "\"SupersededAtUtc\" IS NULL OR \"SupersededAtUtc\" >= \"IssuedAtUtc\"");
                table.HasCheckConstraint("CK_OfflineGrants_Revoked_NotBeforeIssue", "\"RevokedAtUtc\" IS NULL OR \"RevokedAtUtc\" >= \"IssuedAtUtc\"");
            });

        builder.HasKey(grant => new { grant.OrganizationId, grant.Id });
        builder.Property(grant => grant.Id).ValueGeneratedNever();
        builder.Property(grant => grant.OrganizationId).IsRequired();
        builder.Property(grant => grant.UserId).IsRequired();
        builder.Property(grant => grant.IssuedAtUtc).IsRequired();
        builder.Property(grant => grant.AbsoluteExpiresAtUtc).IsRequired();
        builder.Property(grant => grant.RevokedAtUtc);
        builder.Property(grant => grant.SupersededAtUtc);
        builder.Ignore(grant => grant.IsRevoked);

        builder.HasIndex(grant => new { grant.OrganizationId, grant.UserId })
            .HasDatabaseName("IX_OfflineGrants_OneActivePerUser")
            .IsUnique()
            .HasFilter("\"SupersededAtUtc\" IS NULL AND \"RevokedAtUtc\" IS NULL");
        builder.HasIndex(grant => new { grant.OrganizationId, grant.IssuedAtUtc });

        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}

/// <summary>Maps <see cref="OfflineContact"/>: append-only server observations of contact, referencing their grant together with the organization.</summary>
internal sealed class OfflineContactConfiguration : IEntityTypeConfiguration<OfflineContact>
{
    public void Configure(EntityTypeBuilder<OfflineContact> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("OfflineContacts");

        builder.HasKey(contact => new { contact.OrganizationId, contact.Id });
        builder.Property(contact => contact.Id).ValueGeneratedNever();
        builder.Property(contact => contact.OrganizationId).IsRequired();
        builder.Property(contact => contact.GrantId).IsRequired();
        builder.Property(contact => contact.ObservedAtUtc).IsRequired();

        builder.HasOne<OfflineGrant>()
            .WithMany()
            .HasForeignKey(contact => new { contact.OrganizationId, contact.GrantId })
            .HasPrincipalKey(grant => new { grant.OrganizationId, grant.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(contact => new { contact.OrganizationId, contact.GrantId, contact.ObservedAtUtc });
    }
}
