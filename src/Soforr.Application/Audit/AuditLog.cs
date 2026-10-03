using Soforr.Application.Common;
using Soforr.Application.Persistence;
using Soforr.Domain.Audit;

namespace Soforr.Application.Audit;

internal sealed class AuditLog(IAppDbContext db, IClock clock, IIdGenerator ids, ICorrelationContext correlation) : IAuditLog
{
    public void Record(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var auditEvent = AuditEvent.Record(
            ids.NewId(),
            entry.OrganizationId,
            entry.ActorId,
            entry.Action,
            entry.EntityType,
            entry.EntityId,
            entry.Description,
            correlation.CorrelationId,
            clock.UtcNow);

        db.Set<AuditEvent>().Add(auditEvent);
    }
}
