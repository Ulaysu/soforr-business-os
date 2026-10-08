using Soforr.Domain.Offline;

namespace Soforr.Domain.Tests.Offline;

/// <summary>The pure rule of ADR 0008: every boundary, the backdating attack, and the independence from the order of events.</summary>
public class OfflineEventTimePolicyTests
{
    private static readonly OfflinePolicy Policy = new(TimeSpan.FromHours(72), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2));
    private static readonly DateTimeOffset Monday = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int hour, int minute = 0, int day = 0) => Monday.AddDays(day).AddHours(hour).AddMinutes(minute);

    private static DateTimeOffset[] Contacts(params DateTimeOffset[] contacts) => contacts;

    private static EventTimeDecision Decide(
        IReadOnlyCollection<DateTimeOffset> contacts,
        DateTimeOffset eventTime,
        DateTimeOffset received,
        DateTimeOffset? issued = null,
        DateTimeOffset? absoluteExpiry = null,
        DateTimeOffset? revoked = null,
        DateTimeOffset? superseded = null,
        OfflinePolicy? policy = null) =>
        OfflineEventTimePolicy.Decide(issued ?? At(10), absoluteExpiry ?? At(10, day: 30), revoked, superseded, contacts, eventTime, received, policy ?? Policy);

    // ---- the legitimate gap

    [Fact]
    public void ASaleInsideAnOpenSilentGap_IsVouchedFor()
    {
        // Contacts 10:00 .. 10:15, offline, sale at 11:00, synchronized at 11:03 before any new contact.
        var decision = Decide(Contacts(At(10), At(10, 5), At(10, 10), At(10, 15)), At(11), received: At(11, 3));

        Assert.True(decision.Accepted);
        Assert.Equal((At(10, 15), At(11, 3)), (decision.GapStartUtc, decision.GapEndUtc));
        Assert.Equal(TimeSpan.FromMinutes(48), decision.GapLength);
    }

    [Fact]
    public void TheSameSale_IsVouchedFor_WhenTheContactThatClosesTheGapArrivedFirst()
    {
        // Contact at 11:05 closed the gap 10:15 -> 11:05 before the sale arrives at 11:07.
        var decision = Decide(Contacts(At(10), At(10, 5), At(10, 10), At(10, 15), At(11, 5)), At(11), received: At(11, 7));

        Assert.True(decision.Accepted);
        Assert.Equal((At(10, 15), At(11, 5)), (decision.GapStartUtc, decision.GapEndUtc));
    }

    [Fact]
    public void TheOrderOfTheSaleAndTheClosingContact_DoesNotChangeTheOutcome()
    {
        var before = Decide(Contacts(At(10), At(10, 15)), At(11), received: At(11, 3));
        var after = Decide(Contacts(At(10), At(10, 15), At(11, 3)), At(11), received: At(11, 3, day: 0).AddSeconds(5));

        Assert.True(before.Accepted);
        Assert.True(after.Accepted);
    }

    // ---- the backdating attack

    [Fact]
    public void BackdatingIntoAPeriodWhenTheServerSawContact_IsRefused_EvenWithinTheAgeWindow()
    {
        // The device was in contact Monday 10:00 to 12:00 every 5 minutes, then silent until Tuesday 14:00 when it sells.
        // A request on Tuesday 15:00 claims Monday 11:00: only 28 hours old, but the server saw the device then.
        var contacts = Enumerable.Range(0, 25).Select(i => At(10).AddMinutes(i * 5)).ToArray();

        var decision = Decide(contacts, At(11), received: At(15, day: 1));

        Assert.Equal(EventTimeRefusal.Unverifiable, decision.Refusal);
    }

    [Fact]
    public void TimestampAgeAlone_IsNeverEnough_WithNoContactEvidenceAtAll()
    {
        // Only the issuing is known; the claimed time is before the grant existed.
        var decision = Decide(Contacts(), At(9), received: At(15), issued: At(10));

        Assert.Equal(EventTimeRefusal.Unverifiable, decision.Refusal);
    }

    [Fact]
    public void ATimeInsideAShortBlip_IsRefused_NeverTrusted()
    {
        // Contacts 5 minutes apart: no gap, so nothing between them can be vouched for.
        var decision = Decide(Contacts(At(10), At(10, 5), At(10, 10), At(10, 15), At(10, 20)), At(10, 7), received: At(11));

        Assert.Equal(EventTimeRefusal.Unverifiable, decision.Refusal);
    }

    // ---- exact boundaries

    [Fact]
    public void TheGapStart_IsInclusive_AndTheNextContact_IsExclusive()
    {
        var contacts = Contacts(At(10), At(11));

        Assert.True(Decide(contacts, At(10), received: At(12)).Accepted);
        Assert.True(Decide(contacts, At(10).AddTicks(1), received: At(12)).Accepted);
        Assert.True(Decide(contacts, At(11).AddTicks(-1), received: At(12)).Accepted);
    }

    [Fact]
    public void ATimeExactlyAtTheContactThatClosesTheGap_IsNotInTheGap()
    {
        // At 11:00 the device was in contact: the gap [10:00, 11:00) does not include it; the next gap does not start before 11:00 either.
        var decision = Decide(Contacts(At(10), At(11), At(11, 5)), At(11), received: At(12));

        Assert.Equal(EventTimeRefusal.Unverifiable, decision.Refusal);
    }

    [Fact]
    public void AGapOfExactlyTheMinimum_IsNotAGap_ButOneTickMoreIs()
    {
        var exactly = Decide(Contacts(At(10), At(10, 10)), At(10, 5), received: At(10, 20), issued: At(10));
        var more = Decide(Contacts(At(10), At(10, 10).AddTicks(1)), At(10, 5), received: At(10, 20), issued: At(10));

        Assert.Equal(EventTimeRefusal.Unverifiable, exactly.Refusal);
        Assert.True(more.Accepted);
    }

    [Fact]
    public void ExactlySeventyTwoHoursAfterTheGapBegan_IsValid_AndOneTickMoreIsNot()
    {
        var contacts = Contacts(At(10));
        var received = At(10, day: 5);

        var exactly = Decide(contacts, At(10, day: 3), received);
        var beyond = Decide(contacts, At(10, day: 3).AddTicks(1), received);

        Assert.True(exactly.Accepted);
        Assert.Equal(EventTimeRefusal.OutsideWindow, beyond.Refusal);
    }

    [Fact]
    public void TheWindow_IsMeasuredFromTheGapStart_NotFromTheFirstContact()
    {
        // Hourly contact for 48 hours (until Wednesday 10:00); silent after. The 72 hours run from Wednesday 10:00.
        var contacts = Enumerable.Range(0, 49).Select(i => At(10).AddMinutes(i * 60)).ToArray();

        var decision = Decide(contacts, At(10, day: 4), received: At(12, day: 4));

        Assert.True(decision.Accepted);
        Assert.Equal(At(10, day: 2), decision.GapStartUtc);
    }

    [Fact]
    public void AFutureTime_BeyondTheSkew_IsRefusedAsFuture_AndTheSkewAllowanceItselfIsExactlyTheLimit()
    {
        var contacts = Contacts(At(10));
        var received = At(12);

        var atLimit = Decide(contacts, received + Policy.ClockSkew, received);
        var pastLimit = Decide(contacts, received + Policy.ClockSkew + TimeSpan.FromTicks(1), received);

        Assert.Equal(EventTimeRefusal.InFuture, pastLimit.Refusal);
        Assert.NotEqual(EventTimeRefusal.InFuture, atLimit.Refusal);
    }

    [Fact]
    public void TheClockSkew_NeverExtendsTheOpenGap_PastTheReceiptTime()
    {
        // 30 seconds ahead of the server is within the skew allowance, but the gap ends at receipt: not vouched for.
        var received = At(12);

        var decision = Decide(Contacts(At(10)), received.AddSeconds(30), received);

        Assert.Equal(EventTimeRefusal.Unverifiable, decision.Refusal);
    }

    [Fact]
    public void TheClockSkew_NeverLengthensTheSeventyTwoHourWindow()
    {
        var contacts = Contacts(At(10));
        var received = At(10, day: 3).AddMinutes(1);

        var decision = Decide(contacts, At(10, day: 3).AddSeconds(30), received);

        Assert.Equal(EventTimeRefusal.OutsideWindow, decision.Refusal);
    }

    [Fact]
    public void TheWindow_IsConfigurable_AndTheNewValueIsTheLimit()
    {
        var shorter = new OfflinePolicy(TimeSpan.FromHours(1), Policy.MinGap, Policy.ClockSkew);

        Assert.True(Decide(Contacts(At(10)), At(11), At(13), policy: shorter).Accepted);
        Assert.Equal(EventTimeRefusal.OutsideWindow, Decide(Contacts(At(10)), At(11).AddTicks(1), At(13), policy: shorter).Refusal);
    }

    // ---- the grant

    [Fact]
    public void ARevokedGrant_VouchesForNothing()
    {
        var decision = Decide(Contacts(At(10)), At(11), At(12), revoked: At(11, 30));

        Assert.Equal(EventTimeRefusal.GrantRevoked, decision.Refusal);
    }

    [Fact]
    public void AGrantPastItsAbsoluteExpiry_VouchesForNothingAfterIt()
    {
        var expiry = At(12);

        Assert.True(Decide(Contacts(At(10)), expiry, At(13), absoluteExpiry: expiry).Accepted);
        Assert.Equal(EventTimeRefusal.GrantExpired, Decide(Contacts(At(10)), expiry.AddTicks(1), At(13), absoluteExpiry: expiry).Refusal);
    }

    [Fact]
    public void ASupersededGrant_VouchesOnlyUpToTheMomentItWasReplaced()
    {
        var superseded = At(12);
        var contacts = Contacts(At(10));

        Assert.True(Decide(contacts, At(11), At(15), superseded: superseded).Accepted);
        Assert.True(Decide(contacts, superseded, At(15), superseded: superseded).Accepted);
        Assert.Equal(EventTimeRefusal.GrantSuperseded, Decide(contacts, superseded.AddTicks(1), At(15), superseded: superseded).Refusal);
    }

    [Fact]
    public void ATimeBeforeTheGrantWasIssued_IsNotVouchedFor()
    {
        Assert.Equal(EventTimeRefusal.Unverifiable, Decide(Contacts(), At(9, 59), At(12), issued: At(10)).Refusal);
    }

    [Fact]
    public void TheRefusalOrder_IsRevoked_Future_Expired_Superseded_ThenTheGap()
    {
        // Every refusal applies at once; the first in the documented order is reported.
        var all = Decide(Contacts(At(10)), At(20), At(12), absoluteExpiry: At(11), revoked: At(11), superseded: At(11));
        var withoutRevoked = Decide(Contacts(At(10)), At(20), At(12), absoluteExpiry: At(11), superseded: At(11));
        var withoutFuture = Decide(Contacts(At(10)), At(12), At(13), absoluteExpiry: At(11), superseded: At(11));

        Assert.Equal(EventTimeRefusal.GrantRevoked, all.Refusal);
        Assert.Equal(EventTimeRefusal.InFuture, withoutRevoked.Refusal);
        Assert.Equal(EventTimeRefusal.GrantExpired, withoutFuture.Refusal);
    }

    [Fact]
    public void TheInputOrderOfTheContacts_DoesNotMatter_AndDuplicatesAreHarmless()
    {
        var ordered = Decide(Contacts(At(10), At(10, 5), At(11, 30)), At(11), At(12));
        var shuffled = Decide(Contacts(At(11, 30), At(10, 5), At(10), At(10, 5)), At(11), At(12));

        Assert.Equal(ordered, shuffled);
        Assert.True(ordered.Accepted);
    }

    // ---- contact, and what can still be vouched for

    [Fact]
    public void ContactIsRefused_ForARevokedSupersededOrExpiredGrant_AndAcceptedOtherwise()
    {
        var expiry = At(10, day: 30);

        Assert.Equal(EventTimeRefusal.None, OfflineEventTimePolicy.ContactRefusal(expiry, null, null, At(10), At(11), Policy));
        Assert.Equal(EventTimeRefusal.GrantRevoked, OfflineEventTimePolicy.ContactRefusal(expiry, At(10), null, At(10), At(11), Policy));
        Assert.Equal(EventTimeRefusal.GrantSuperseded, OfflineEventTimePolicy.ContactRefusal(expiry, null, At(10), At(10), At(11), Policy));
        Assert.Equal(EventTimeRefusal.GrantExpired, OfflineEventTimePolicy.ContactRefusal(expiry, null, null, At(10), expiry.AddTicks(1), Policy));
    }

    [Fact]
    public void ContactIsRefusedAfterMoreThanTheWindowOfSilence_ButNotAtExactlyTheWindow()
    {
        Assert.Equal(EventTimeRefusal.None, OfflineEventTimePolicy.ContactRefusal(At(10, day: 30), null, null, At(10), At(10, day: 3), Policy));
        Assert.Equal(EventTimeRefusal.GrantExpired, OfflineEventTimePolicy.ContactRefusal(At(10, day: 30), null, null, At(10), At(10, day: 3).AddTicks(1), Policy));
    }

    [Fact]
    public void TheLastVouchableTime_IsTheLastContactPlusTheWindow_CappedByExpiryReplacementAndRevocation()
    {
        var last = At(10);

        Assert.Equal(At(10, day: 3), OfflineEventTimePolicy.LastVouchableTime(At(10, day: 30), null, null, last, Policy));
        Assert.Equal(At(10, day: 1), OfflineEventTimePolicy.LastVouchableTime(At(10, day: 1), null, null, last, Policy));
        Assert.Equal(At(12), OfflineEventTimePolicy.LastVouchableTime(At(10, day: 30), null, At(12), last, Policy));
        Assert.Equal(At(11), OfflineEventTimePolicy.LastVouchableTime(At(10, day: 30), At(11), At(12), last, Policy));
    }
}
