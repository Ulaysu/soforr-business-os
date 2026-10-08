using Microsoft.EntityFrameworkCore;
using Soforr.Application.Persistence;
using Soforr.Domain.Offline;

namespace Soforr.Application.Offline;

/// <summary>
/// Removes the offline evidence (grants and their contacts) that can no longer vouch for anything and is older than the retention
/// period. A grant is kept while an operation recorded under it could still be replayed (until its last vouchable time, see
/// <see cref="OfflineEventTimePolicy.LastVouchableTime"/>) and then for <see cref="IOfflineSettings.EvidenceRetention"/>
/// more, so a late replay still finds its evidence; sales keep the grant id as provenance without a foreign key, so pruning never
/// touches them. It runs through the tenant-filtered context, so it only ever reaches the caller's organization (no filter bypass,
/// no background job): every issuance of a grant prunes that organization. An organization that issues no grants keeps no
/// growing data. Nothing is pruned that the policy could still use.
/// </summary>
internal static class OfflineEvidencePruner
{
    public static async Task<int> PruneAsync(IAppDbContext db, DateTimeOffset now, IOfflineSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(settings);

        // A grant issued within the retention period cannot be prunable yet: cheap prefilter.
        var candidates = await db.Set<OfflineGrant>().AsNoTracking().Where(g => g.IssuedAtUtc < now - settings.EvidenceRetention).ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var ids = candidates.Select(g => g.Id).ToList();
        var lastContacts = await db.Set<OfflineContact>().AsNoTracking()
            .Where(c => ids.Contains(c.GrantId))
            .GroupBy(c => c.GrantId)
            .Select(group => new { GrantId = group.Key, Last = group.Max(c => c.ObservedAtUtc) })
            .ToDictionaryAsync(x => x.GrantId, x => x.Last, cancellationToken);

        var expired = candidates
            .Where(grant => OfflineEventTimePolicy.LastVouchableTime(
                grant.AbsoluteExpiresAtUtc,
                grant.RevokedAtUtc,
                grant.SupersededAtUtc,
                lastContacts.GetValueOrDefault(grant.Id, grant.IssuedAtUtc),
                settings.Policy) + settings.EvidenceRetention < now)
            .Select(grant => grant.Id)
            .ToList();

        if (expired.Count == 0)
        {
            return 0;
        }

        await db.Set<OfflineContact>().Where(c => expired.Contains(c.GrantId)).ExecuteDeleteAsync(cancellationToken);
        return await db.Set<OfflineGrant>().Where(g => expired.Contains(g.Id)).ExecuteDeleteAsync(cancellationToken);
    }
}
