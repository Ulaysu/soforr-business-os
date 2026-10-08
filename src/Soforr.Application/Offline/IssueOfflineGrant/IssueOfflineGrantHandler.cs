using Microsoft.EntityFrameworkCore;
using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Offline;

namespace Soforr.Application.Offline.IssueOfflineGrant;

/// <summary>
/// Asks the server for an offline grant (ADR 0008). The command carries nothing: the user and organization come from the
/// authenticated session, the identifier and every time from the server.
/// </summary>
public sealed record IssueOfflineGrantCommand;

/// <summary>
/// Issues the caller's offline grant. In one transaction it supersedes the caller's previous active grant (a user has one at a time, so
/// the server's observations of contact come from one stream), records the first contact, and writes the audit event. It also prunes
/// the organization's old offline evidence (see <see cref="OfflineEvidencePruner"/>). Two issuances at the same moment cannot both
/// stay active: the loser is retried, re-reading what the winner wrote.
/// <para>
/// The permission: a user who may record sales or payments may hold a grant. The grant authorizes nothing by itself; every offline
/// sale and payment still needs its own permission (<c>SalesCreate</c>, <c>PaymentsRecord</c>) when it is replayed.
/// </para>
/// </summary>
public sealed class IssueOfflineGrantHandler(IAppDbContext db, IClock clock, IIdGenerator ids, ICurrentUser user, IOfflineSettings settings, IAuditLog audit)
    : ICommandHandler<IssueOfflineGrantCommand, OfflineGrantDto>
{
    private const int MaxAttempts = 3;

    public async Task<Result<OfflineGrantDto>> HandleAsync(IssueOfflineGrantCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (OfflineAccess.Denied(user) is { } denied)
        {
            return Result.Failure<OfflineGrantDto>(denied);
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return Result.Success(await IssueOnceAsync(cancellationToken));
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                db.ClearChangeTracker();
                await Task.Delay(Random.Shared.Next(5, 40), cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.ClearChangeTracker();
                return Result.Failure<OfflineGrantDto>(new Error("offline.concurrency_conflict", ErrorKind.Conflict, "The grant was changed by another request. Try again."));
            }
        }
    }

    private async Task<OfflineGrantDto> IssueOnceAsync(CancellationToken cancellationToken)
    {
        db.ClearChangeTracker();
        var organizationId = user.OrganizationId!.Value;
        var userId = user.UserId!.Value;
        var now = clock.UtcNow;

        // Filtered to the caller's organization.
        var previous = await db.Set<OfflineGrant>().Where(g => g.UserId == userId && g.SupersededAtUtc == null && g.RevokedAtUtc == null).ToListAsync(cancellationToken);
        foreach (var old in previous)
        {
            old.Supersede(now);
        }

        var grant = OfflineGrant.Issue(ids.NewId(), organizationId, userId, now, now + settings.GrantMaxAge);
        db.Set<OfflineGrant>().Add(grant);
        db.Set<OfflineContact>().Add(new OfflineContact(ids.NewId(), organizationId, grant.Id, now));

        audit.Record(new AuditEntry(organizationId, AuditActions.OfflineGrantIssued, userId, "OfflineGrant", grant.Id, "An offline grant was issued."));
        await OfflineEvidencePruner.PruneAsync(db, now, settings, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return OfflineGrantDto.From(grant, now, now, settings);
    }
}
