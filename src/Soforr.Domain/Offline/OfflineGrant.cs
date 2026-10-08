using Soforr.Domain.Common;

namespace Soforr.Domain.Offline;

/// <summary>
/// The server-side record that a user's device may keep working during a connectivity outage (ADR 0007, 0008). It is an
/// <b>identifier, not a secret</b>: it authorizes nothing by itself and is only meaningful inside an authenticated session of
/// the same user and organization, both taken from the stored record and never from a request.
/// <para>
/// A user has at most one active grant: issuing a new one <see cref="Supersede"/>s the previous one at that moment, so the
/// server's observations of contact come from one stream per user. A superseded grant still vouches for event times up to the
/// moment it was superseded; a revoked grant vouches for nothing. The grant lives at most until
/// <see cref="AbsoluteExpiresAtUtc"/>. The contacts that make up the evidence are <see cref="OfflineContact"/> records.
/// </para>
/// </summary>
public sealed class OfflineGrant
{
    // Required by the persistence layer for materialization; not part of the domain API.
    private OfflineGrant()
    {
    }

    private OfflineGrant(Guid id, Guid organizationId, Guid userId, DateTimeOffset issuedAtUtc, DateTimeOffset absoluteExpiresAtUtc)
    {
        Id = id;
        OrganizationId = organizationId;
        UserId = userId;
        IssuedAtUtc = issuedAtUtc;
        AbsoluteExpiresAtUtc = absoluteExpiresAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>When the server issued the grant (server clock). Also the first contact.</summary>
    public DateTimeOffset IssuedAtUtc { get; private set; }

    /// <summary>The latest moment the grant can vouch for, whatever contact there is.</summary>
    public DateTimeOffset AbsoluteExpiresAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    /// <summary>When a newer grant of the same user replaced this one; event times after it are not vouched for.</summary>
    public DateTimeOffset? SupersededAtUtc { get; private set; }

    public bool IsRevoked => RevokedAtUtc is not null;

    public static OfflineGrant Issue(Guid id, Guid organizationId, Guid userId, DateTimeOffset issuedAtUtc, DateTimeOffset absoluteExpiresAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("offline_grant.id_required", "A grant must have an identifier.");
        }

        if (organizationId == Guid.Empty || userId == Guid.Empty)
        {
            throw new DomainException("offline_grant.owner_required", "A grant must belong to an organization and a user.");
        }

        RequireUtc(issuedAtUtc);
        RequireUtc(absoluteExpiresAtUtc);

        if (absoluteExpiresAtUtc <= issuedAtUtc)
        {
            throw new DomainException("offline_grant.lifetime_invalid", "A grant must expire after it is issued.");
        }

        return new OfflineGrant(id, organizationId, userId, issuedAtUtc, absoluteExpiresAtUtc);
    }

    /// <summary>The user has a newer grant. Idempotent: the first time counts.</summary>
    public void Supersede(DateTimeOffset atUtc)
    {
        RequireUtc(atUtc);
        SupersededAtUtc ??= atUtc;
    }

    /// <summary>The grant can no longer be used, for new contact or for any event time. Idempotent: the first time counts.</summary>
    public void Revoke(DateTimeOffset atUtc)
    {
        RequireUtc(atUtc);
        RevokedAtUtc ??= atUtc;
    }

    private static void RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new DomainException("offline_grant.timestamp_not_utc", "Timestamps must be in UTC.");
        }
    }
}

/// <summary>
/// One observation by the server that the user's device was in contact, with the server's own clock. Append-only: contacts are
/// the evidence that decides which event times can be vouched for, so they are never changed (pruning removes whole old
/// grants, see the pruner). A client time is never stored here.
/// </summary>
public sealed class OfflineContact
{
    // Required by the persistence layer for materialization; not part of the domain API.
    private OfflineContact()
    {
    }

    public OfflineContact(Guid id, Guid organizationId, Guid grantId, DateTimeOffset observedAtUtc)
    {
        if (id == Guid.Empty || organizationId == Guid.Empty || grantId == Guid.Empty)
        {
            throw new DomainException("offline_contact.ids_required", "A contact must have an identifier, an organization and a grant.");
        }

        if (observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new DomainException("offline_contact.timestamp_not_utc", "Timestamps must be in UTC.");
        }

        Id = id;
        OrganizationId = organizationId;
        GrantId = grantId;
        ObservedAtUtc = observedAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid GrantId { get; private set; }

    public DateTimeOffset ObservedAtUtc { get; private set; }
}
