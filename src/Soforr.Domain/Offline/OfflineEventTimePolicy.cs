namespace Soforr.Domain.Offline;

/// <summary>The policy numbers of ADR 0008, from configuration: the offline window (72 hours), the shortest silence that counts as a gap, and the clock-skew allowance.</summary>
public sealed record OfflinePolicy(TimeSpan Window, TimeSpan MinGap, TimeSpan ClockSkew);

/// <summary>Why an offline event time was not vouched for. Each maps to a stable error code in the Application.</summary>
public enum EventTimeRefusal
{
    None = 0,

    /// <summary>The grant was revoked: it vouches for nothing.</summary>
    GrantRevoked = 1,

    /// <summary>The time is later than the server's receipt time plus the clock-skew allowance.</summary>
    InFuture = 2,

    /// <summary>The time is after the grant's absolute expiry.</summary>
    GrantExpired = 3,

    /// <summary>The time is after a newer grant of the same user replaced this one.</summary>
    GrantSuperseded = 4,

    /// <summary>The time is not inside any silent gap the server observed on this grant (or before the grant existed).</summary>
    Unverifiable = 5,

    /// <summary>The time is inside a silent gap but more than the offline window after the gap began.</summary>
    OutsideWindow = 6,
}

/// <summary>The outcome of judging an event time. When accepted, <see cref="GapStartUtc"/> and <see cref="GapEndUtc"/> are the silent gap that covers it.</summary>
public sealed record EventTimeDecision(EventTimeRefusal Refusal, DateTimeOffset? GapStartUtc, DateTimeOffset? GapEndUtc)
{
    public bool Accepted => Refusal == EventTimeRefusal.None;

    public TimeSpan? GapLength => Accepted ? GapEndUtc - GapStartUtc : null;
}

/// <summary>
/// <b>Is this offline event time vouched for?</b> The pure rule of ADR 0008, with no clock, database or framework: everything is
/// passed in, so every boundary is a unit test.
/// <para>
/// <b>Principle: timestamp age is not authorization.</b> A time is vouched for only if the server itself observed that this grant's
/// device was silent over it. The server never learns the device's clock or connection; what it has is the instants at which it
/// observed contact (<paramref name="contactsUtc"/>, server clock). It cannot tell a device that is really disconnected from a user
/// who stays silent on purpose; it can refuse every time at which it saw the device in contact, every time outside the grant, and
/// every time more than the window after the silence began.
/// </para>
/// <para>
/// <b>Silent gaps.</b> Between two consecutive contacts a and b, the gap is <c>[a, b)</c> when <c>b - a</c> is longer than
/// <see cref="OfflinePolicy.MinGap"/>. The lower bound is inclusive (the device was last seen at a); the upper bound is exclusive
/// (at b it was in contact). After the last contact the <b>open gap</b> is <c>[last, end]</c> with
/// <c>end = min(receivedAt, supersededAt, absoluteExpiry)</c>, inclusive, and it counts only if longer than the minimum gap, so the
/// answer does not depend on whether the next contact arrives before or after the sale does: closing a gap later never removes
/// the time it covered. Gaps shorter than the minimum are not gaps (a sale made in such a blip is refused, never trusted).
/// </para>
/// <para>
/// <b>Window.</b> <c>t - gapStart &lt;= Window</c>: exactly the window is valid, one tick more is not. <b>Future.</b>
/// <c>t &lt;= receivedAt + ClockSkew</c>. The skew only separates "the client clock is ahead" from the other refusals; it never
/// lengthens a gap or the window (an open gap ends at receipt, not at receipt plus skew). The checks run in a fixed order:
/// revoked, future, expired, superseded, then the gap and window.
/// </para>
/// </summary>
public static class OfflineEventTimePolicy
{
    public static EventTimeDecision Decide(
        DateTimeOffset grantIssuedAtUtc,
        DateTimeOffset grantAbsoluteExpiresAtUtc,
        DateTimeOffset? grantRevokedAtUtc,
        DateTimeOffset? grantSupersededAtUtc,
        IReadOnlyCollection<DateTimeOffset> contactsUtc,
        DateTimeOffset eventTimeUtc,
        DateTimeOffset receivedAtUtc,
        OfflinePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(contactsUtc);
        ArgumentNullException.ThrowIfNull(policy);

        if (grantRevokedAtUtc is not null)
        {
            return Refused(EventTimeRefusal.GrantRevoked);
        }

        if (eventTimeUtc > receivedAtUtc + policy.ClockSkew)
        {
            return Refused(EventTimeRefusal.InFuture);
        }

        if (eventTimeUtc > grantAbsoluteExpiresAtUtc)
        {
            return Refused(EventTimeRefusal.GrantExpired);
        }

        if (grantSupersededAtUtc is { } superseded && eventTimeUtc > superseded)
        {
            return Refused(EventTimeRefusal.GrantSuperseded);
        }

        if (eventTimeUtc < grantIssuedAtUtc)
        {
            return Refused(EventTimeRefusal.Unverifiable);
        }

        // The issuing is the first contact, so the stream always starts there even if a row is missing.
        var observed = contactsUtc.Append(grantIssuedAtUtc).Distinct().Order().ToList();

        for (var index = 0; index + 1 < observed.Count; index++)
        {
            var start = observed[index];
            var end = observed[index + 1];
            if (end - start > policy.MinGap && eventTimeUtc >= start && eventTimeUtc < end)
            {
                return Covered(start, end, eventTimeUtc, policy);
            }
        }

        var last = observed[^1];
        var openEnd = Min(receivedAtUtc, grantSupersededAtUtc, grantAbsoluteExpiresAtUtc);
        if (openEnd - last > policy.MinGap && eventTimeUtc >= last && eventTimeUtc <= openEnd)
        {
            return Covered(last, openEnd, eventTimeUtc, policy);
        }

        return Refused(EventTimeRefusal.Unverifiable);
    }

    /// <summary>
    /// Whether the grant can still take a new contact at <paramref name="nowUtc"/>: not revoked, not superseded, not past its
    /// absolute expiry, and not silent for longer than the window since the last contact (a grant that silent has expired; the
    /// device asks for a new one). Older gaps stay valid for replay regardless.
    /// </summary>
    public static EventTimeRefusal ContactRefusal(
        DateTimeOffset grantAbsoluteExpiresAtUtc,
        DateTimeOffset? grantRevokedAtUtc,
        DateTimeOffset? grantSupersededAtUtc,
        DateTimeOffset lastContactUtc,
        DateTimeOffset nowUtc,
        OfflinePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (grantRevokedAtUtc is not null)
        {
            return EventTimeRefusal.GrantRevoked;
        }

        if (grantSupersededAtUtc is not null)
        {
            return EventTimeRefusal.GrantSuperseded;
        }

        return nowUtc > grantAbsoluteExpiresAtUtc || nowUtc - lastContactUtc > policy.Window ? EventTimeRefusal.GrantExpired : EventTimeRefusal.None;
    }

    /// <summary>
    /// The latest event time this grant could ever vouch for, used to decide when its evidence may be pruned: the last contact plus
    /// the window, never past the absolute expiry, the time it was superseded or the time it was revoked.
    /// </summary>
    public static DateTimeOffset LastVouchableTime(
        DateTimeOffset grantAbsoluteExpiresAtUtc,
        DateTimeOffset? grantRevokedAtUtc,
        DateTimeOffset? grantSupersededAtUtc,
        DateTimeOffset lastContactUtc,
        OfflinePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var latest = Min(lastContactUtc + policy.Window, grantSupersededAtUtc, grantAbsoluteExpiresAtUtc);
        return grantRevokedAtUtc is { } revoked && revoked < latest ? revoked : latest;
    }

    private static EventTimeDecision Covered(DateTimeOffset start, DateTimeOffset end, DateTimeOffset eventTimeUtc, OfflinePolicy policy) =>
        eventTimeUtc - start > policy.Window ? Refused(EventTimeRefusal.OutsideWindow) : new EventTimeDecision(EventTimeRefusal.None, start, end);

    private static EventTimeDecision Refused(EventTimeRefusal refusal) => new(refusal, null, null);

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset? second, DateTimeOffset third)
    {
        var smallest = first < third ? first : third;
        return second is { } value && value < smallest ? value : smallest;
    }
}
