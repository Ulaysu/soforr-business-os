using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Offline;

namespace Soforr.Application.Offline;

/// <summary>The offline settings (ADR 0007, 0008), supplied by Infrastructure from configuration with safe defaults.</summary>
public interface IOfflineSettings
{
    /// <summary>The window, the minimum silent gap and the clock-skew allowance of ADR 0008.</summary>
    OfflinePolicy Policy { get; }

    /// <summary>The longest a single grant lives, however much contact there is (default 30 days).</summary>
    TimeSpan GrantMaxAge { get; }

    /// <summary>How long the evidence of a grant is kept after the last time it could vouch for (default 90 days). A replay after that is refused as an unknown grant.</summary>
    TimeSpan EvidenceRetention { get; }

    /// <summary>How often the device is asked to report contact, shown to clients (default 5 minutes); not enforced.</summary>
    TimeSpan ContactInterval { get; }
}

/// <summary>
/// The stable error taxonomy of the offline contract. A client classifies a response by status and code: <b>authentication
/// required</b> (401, sign in and retry later), <b>retry</b> (429 after <c>Retry-After</c>, 5xx, no network), <b>terminal</b> (400,
/// 403, 404: fix or give up), <b>conflict, needs attention</b> (409 and the business 422s: a person decides). Nothing here ever
/// falls back to server time.
/// </summary>
internal static class OfflineErrors
{
    public static Error GrantNotFound { get; } = new("offline.grant_not_found", ErrorKind.NotFound, "The offline grant was not found. Ask for a new one while online.");

    public static Error GrantRequired { get; } = new("offline.grant_required", ErrorKind.Validation, "An event time is accepted only together with an offline grant.", "offlineGrantId");

    public static Error EventTimeRequired { get; } = new("offline.event_time_required", ErrorKind.Validation, "An offline grant needs the time the event happened.", "occurredAtUtc");

    public static Error GrantRevoked { get; } = new("offline.grant_revoked", ErrorKind.Conflict, "The offline grant was revoked. Sign in and ask for a new one while online.");

    public static Error GrantExpired { get; } = new("offline.grant_expired", ErrorKind.Conflict, "The offline grant has expired. Ask for a new one while online.");

    public static Error GrantSuperseded { get; } = new("offline.grant_superseded", ErrorKind.Conflict, "A newer offline grant replaced this one. Use the newer grant while online.");

    public static Error EventTimeInFuture { get; } = new("offline.event_time_in_future", ErrorKind.Validation, "The event time is in the future. Check the clock of the device.", "occurredAtUtc");

    public static Error EventTimeUnverifiable { get; } = new("offline.event_time_unverifiable", ErrorKind.Conflict, "The server cannot vouch for this event time: it did not observe the device being out of contact then. Nothing was recorded.", "occurredAtUtc");

    public static Error EventTimeOutsideWindow { get; } = new("offline.event_time_outside_window", ErrorKind.Conflict, "The event time is too long after the device was last in contact. Nothing was recorded.", "occurredAtUtc");

    public static Error From(EventTimeRefusal refusal) => refusal switch
    {
        EventTimeRefusal.GrantRevoked => GrantRevoked,
        EventTimeRefusal.InFuture => EventTimeInFuture,
        EventTimeRefusal.GrantExpired => GrantExpired,
        EventTimeRefusal.GrantSuperseded => GrantSuperseded,
        EventTimeRefusal.OutsideWindow => EventTimeOutsideWindow,
        _ => EventTimeUnverifiable,
    };

    public static string Code(EventTimeRefusal refusal) => From(refusal).Code;
}

/// <summary>What the server established for an accepted offline event.</summary>
public sealed record AcceptedOfflineEvent(Guid GrantId, DateTimeOffset EventTimeUtc, DateTimeOffset ReceivedAtUtc, DateTimeOffset GapStartUtc, DateTimeOffset GapEndUtc);

/// <summary>
/// Judges an offline event time for the sale and payment handlers (ADR 0008). The user and organization come from the
/// authenticated session; the grant is looked up through the tenant-filtered context and must belong to that user, so another
/// user's or organization's grant is simply not found. The evidence is the server's own record of contacts; nothing the client
/// says about time or identity is trusted. A refusal is audited (the grant, the times, the reason: no personal data) and never
/// replaced by server time; the caller records nothing and reserves no id.
/// </summary>
public sealed partial class OfflineEventTimeValidator(IAppDbContext db, IClock clock, ICurrentUser user, IOfflineSettings settings, IAuditLog audit, ILogger<OfflineEventTimeValidator>? logger = null)
{
    public async Task<Result<AcceptedOfflineEvent>> ValidateAsync(Guid? grantId, DateTimeOffset eventTimeUtc, string operation, CancellationToken cancellationToken)
    {
        if (grantId is not { } id || id == Guid.Empty)
        {
            return Result.Failure<AcceptedOfflineEvent>(OfflineErrors.GrantRequired);
        }

        var organizationId = user.OrganizationId!.Value;
        var receivedAt = clock.UtcNow;

        // Filtered to the caller's organization, and then to the caller: a grant of anyone else behaves as not found.
        var grant = await db.Set<OfflineGrant>().AsNoTracking().SingleOrDefaultAsync(g => g.Id == id && g.UserId == user.UserId!.Value, cancellationToken);
        if (grant is null)
        {
            return Result.Failure<AcceptedOfflineEvent>(OfflineErrors.GrantNotFound);
        }

        var contacts = await db.Set<OfflineContact>().AsNoTracking().Where(c => c.GrantId == id).Select(c => c.ObservedAtUtc).ToListAsync(cancellationToken);
        var decision = OfflineEventTimePolicy.Decide(grant.IssuedAtUtc, grant.AbsoluteExpiresAtUtc, grant.RevokedAtUtc, grant.SupersededAtUtc, contacts, eventTimeUtc, receivedAt, settings.Policy);

        if (!decision.Accepted)
        {
            audit.Record(new AuditEntry(
                organizationId,
                AuditActions.OfflineEventRefused,
                user.UserId,
                "OfflineGrant",
                id,
                string.Create(CultureInfo.InvariantCulture, $"An offline {operation} was refused ({OfflineErrors.Code(decision.Refusal)}): event time {eventTimeUtc:O}, received {receivedAt:O}.")));
            await db.SaveChangesAsync(cancellationToken);
            var code = OfflineErrors.Code(decision.Refusal);
            LogRefused(logger ?? NullLogger<OfflineEventTimeValidator>.Instance, operation, code, id, eventTimeUtc, receivedAt);

            return Result.Failure<AcceptedOfflineEvent>(OfflineErrors.From(decision.Refusal));
        }

        return Result.Success(new AcceptedOfflineEvent(id, eventTimeUtc, receivedAt, decision.GapStartUtc!.Value, decision.GapEndUtc!.Value));
    }

    /// <summary>The audit event for an accepted offline operation, written in the same save as the operation itself.</summary>
    public void RecordAccepted(AcceptedOfflineEvent accepted, string action, Guid entityId, string entityType, string operation)
    {
        ArgumentNullException.ThrowIfNull(accepted);

        var delaySeconds = (accepted.ReceivedAtUtc - accepted.EventTimeUtc).TotalSeconds;
        var gapMinutes = (accepted.GapEndUtc - accepted.GapStartUtc).TotalMinutes;
        LogAccepted(logger ?? NullLogger<OfflineEventTimeValidator>.Instance, operation, accepted.GrantId, delaySeconds, gapMinutes);

        audit.Record(new AuditEntry(
            user.OrganizationId!.Value,
            action,
            user.UserId,
            entityType,
            entityId,
            string.Create(
                CultureInfo.InvariantCulture,
                $"An offline {operation} was accepted under grant {accepted.GrantId:D}: event time {accepted.EventTimeUtc:O}, received {accepted.ReceivedAtUtc:O} (delay {(accepted.ReceivedAtUtc - accepted.EventTimeUtc).TotalSeconds:0} s), silent gap {accepted.GapStartUtc:O} to {accepted.GapEndUtc:O} ({(accepted.GapEndUtc - accepted.GapStartUtc).TotalMinutes:0} min).")));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Offline {Operation} refused: {Code}, grant {GrantId}, event time {EventTime:O}, received {Received:O}")]
    private static partial void LogRefused(ILogger logger, string operation, string code, Guid grantId, DateTimeOffset eventTime, DateTimeOffset received);

    [LoggerMessage(Level = LogLevel.Information, Message = "Offline {Operation} accepted: grant {GrantId}, delay {DelaySeconds} s, silent gap {GapMinutes} min")]
    private static partial void LogAccepted(ILogger logger, string operation, Guid grantId, double delaySeconds, double gapMinutes);
}
