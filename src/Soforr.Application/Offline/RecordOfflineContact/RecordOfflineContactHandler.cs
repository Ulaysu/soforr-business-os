using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Offline;

namespace Soforr.Application.Offline.RecordOfflineContact;

/// <summary>Reports that the device is in contact. The grant is named by <see cref="GrantId"/>; the time is the server's.</summary>
public sealed record RecordOfflineContactCommand(Guid GrantId);

/// <summary>
/// Appends one contact observation (the server clock) to the caller's grant. The grant must be the caller's, not revoked, not
/// superseded, not past its absolute expiry and not silent for longer than the window; otherwise the device asks for a new grant.
/// A contact is an append-only row, so two contacts at the same moment cannot disturb each other or the evidence of earlier gaps,
/// and nothing about an earlier gap changes when this contact closes it. Contacts are not audited one by one (they are frequent
/// and carry no business meaning); issuing, accepting and refusing are.
/// </summary>
public sealed class RecordOfflineContactHandler(IAppDbContext db, IClock clock, IIdGenerator ids, ICurrentUser user, IOfflineSettings settings)
    : ICommandHandler<RecordOfflineContactCommand, OfflineGrantDto>
{
    public async Task<Result<OfflineGrantDto>> HandleAsync(RecordOfflineContactCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (OfflineAccess.Denied(user) is { } denied)
        {
            return Result.Failure<OfflineGrantDto>(denied);
        }

        if (command.GrantId == Guid.Empty)
        {
            return Result.Failure<OfflineGrantDto>(new Error("offline.grant_invalid", ErrorKind.Validation, "The grant id must be a non-empty identifier.", "grantId"));
        }

        // Filtered to the caller's organization, then to the caller.
        var grant = await db.Set<OfflineGrant>().AsNoTracking().SingleOrDefaultAsync(g => g.Id == command.GrantId && g.UserId == user.UserId!.Value, cancellationToken);
        if (grant is null)
        {
            return Result.Failure<OfflineGrantDto>(OfflineErrors.GrantNotFound);
        }

        var now = clock.UtcNow;
        var last = await db.Set<OfflineContact>().Where(c => c.GrantId == grant.Id).MaxAsync(c => (DateTimeOffset?)c.ObservedAtUtc, cancellationToken) ?? grant.IssuedAtUtc;

        var refusal = OfflineEventTimePolicy.ContactRefusal(grant.AbsoluteExpiresAtUtc, grant.RevokedAtUtc, grant.SupersededAtUtc, last, now, settings.Policy);
        if (refusal != EventTimeRefusal.None)
        {
            return Result.Failure<OfflineGrantDto>(OfflineErrors.From(refusal));
        }

        db.Set<OfflineContact>().Add(new OfflineContact(ids.NewId(), grant.OrganizationId, grant.Id, now));
        await db.SaveChangesAsync(cancellationToken);

        return Result.Success(OfflineGrantDto.From(grant, now, now, settings));
    }
}
