using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Soforr.Domain.Audit;
using Soforr.Domain.Organizations;

namespace Soforr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="AuditEvent"/>, the append-only audit trail. Immutability is enforced three times: the Domain has no
/// way to change an event, the context refuses to save a changed or deleted one, and a database trigger (added by the
/// migration) refuses any <c>UPDATE</c>, <c>DELETE</c> or <c>TRUNCATE</c> on the table, even from raw SQL. The CHECK
/// constraints repeat the Domain invariants so data written by anything else cannot break them.
/// </summary>
internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "AuditEvents",
            table =>
            {
                table.HasCheckConstraint("CK_AuditEvents_Action_Format", "\"Action\" ~ '^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$'");
                table.HasCheckConstraint("CK_AuditEvents_Entity_BothOrNeither", "(\"EntityType\" IS NULL) = (\"EntityId\" IS NULL)");
                table.HasCheckConstraint("CK_AuditEvents_EntityType_NotBlank", "\"EntityType\" IS NULL OR char_length(btrim(\"EntityType\")) > 0");
                table.HasCheckConstraint("CK_AuditEvents_Description_NotBlank", "\"Description\" IS NULL OR char_length(btrim(\"Description\")) > 0");
            });

        builder.HasKey(auditEvent => auditEvent.Id);
        builder.Property(auditEvent => auditEvent.Id).ValueGeneratedNever();

        builder.Property(auditEvent => auditEvent.OrganizationId).IsRequired();
        builder.Property(auditEvent => auditEvent.ActorId);

        builder.Property(auditEvent => auditEvent.Action).IsRequired().HasMaxLength(AuditEvent.MaxActionLength);
        builder.Property(auditEvent => auditEvent.EntityType).HasMaxLength(AuditEvent.MaxEntityTypeLength);
        builder.Property(auditEvent => auditEvent.EntityId);
        builder.Property(auditEvent => auditEvent.Description).HasMaxLength(AuditEvent.MaxDescriptionLength);
        builder.Property(auditEvent => auditEvent.CorrelationId).HasMaxLength(AuditEvent.MaxCorrelationIdLength);
        builder.Property(auditEvent => auditEvent.OccurredAtUtc).IsRequired();

        // The trail of an organization, newest first; and the trail of one record.
        builder.HasIndex(auditEvent => new { auditEvent.OrganizationId, auditEvent.OccurredAtUtc, auditEvent.Id });
        builder.HasIndex(auditEvent => new { auditEvent.OrganizationId, auditEvent.EntityType, auditEvent.EntityId });

        // There is deliberately no foreign key from the actor to the user table: an audit event outlives any user.
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(auditEvent => auditEvent.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
