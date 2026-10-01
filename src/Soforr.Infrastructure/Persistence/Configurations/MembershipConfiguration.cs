using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Soforr.Domain.Identity;
using Soforr.Domain.Organizations;
using Soforr.Infrastructure.Identity;

namespace Soforr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Membership"/>. One organization per user in the MVP (ADR 0002) is a unique index on the user,
/// so the database, not only the application, refuses a second membership.
/// </summary>
internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "Memberships",
            table => table.HasCheckConstraint("CK_Memberships_Role_Known", "\"Role\" IN ('Owner', 'Manager', 'Staff')"));

        builder.HasKey(membership => membership.Id);
        builder.Property(membership => membership.Id).ValueGeneratedNever();

        builder.Property(membership => membership.Role)
            .IsRequired()
            .HasMaxLength(20)
            .HasConversion<string>();

        builder.Property(membership => membership.CreatedAtUtc).IsRequired();

        builder.HasIndex(membership => membership.UserId).IsUnique();
        builder.HasIndex(membership => membership.OrganizationId);

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(membership => membership.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(membership => membership.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
