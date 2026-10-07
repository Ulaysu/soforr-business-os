using Soforr.Application.Intelligence;

namespace Soforr.Application.Tests.Intelligence;

public class InsightWindowsTests
{
    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void InUtc_TheWindowIsTheCompleteDaysBeforeToday_HalfOpen()
    {
        var window = InsightWindows.TrailingCompleteDays(Utc(2026, 6, 1, 10), TimeZoneInfo.Utc, 30);

        Assert.Equal(new DateOnly(2026, 5, 2), window.FirstDay);
        Assert.Equal(new DateOnly(2026, 5, 31), window.LastDay);
        Assert.Equal(30, window.Days);
        Assert.Equal(Utc(2026, 5, 2), window.StartUtc);
        Assert.Equal(Utc(2026, 6, 1), window.EndUtc);
        Assert.Equal("UTC", window.TimeZoneId);
        Assert.Equal(TimeSpan.FromDays(30), window.EndUtc - window.StartUtc);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 30)]
    [InlineData(23, 59)]
    public void TheWindow_DoesNotDependOnTheTimeOfDay(int hour, int minute)
    {
        var morning = InsightWindows.TrailingCompleteDays(Utc(2026, 6, 1, 0, 0), TimeZoneInfo.Utc, 30);

        var later = InsightWindows.TrailingCompleteDays(Utc(2026, 6, 1, hour, minute), TimeZoneInfo.Utc, 30);

        Assert.Equal(morning, later);
    }

    [Fact]
    public void AtMidnight_TheWindowMovesOnByOneDay()
    {
        var before = InsightWindows.TrailingCompleteDays(Utc(2026, 6, 1, 23, 59), TimeZoneInfo.Utc, 30);
        var after = InsightWindows.TrailingCompleteDays(Utc(2026, 6, 2, 0, 0), TimeZoneInfo.Utc, 30);

        Assert.Equal(before.StartUtc.AddDays(1), after.StartUtc);
        Assert.Equal(before.EndUtc.AddDays(1), after.EndUtc);
    }

    [Fact]
    public void TheOrganizationsTimeZone_DecidesWhereTheDaysBegin()
    {
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"); // UTC+9, no daylight saving

        // 2026-06-01 10:00 UTC is 19:00 on June 1 in Tokyo; today is June 1 there.
        var window = InsightWindows.TrailingCompleteDays(Utc(2026, 6, 1, 10), tokyo, 30);

        Assert.Equal((new DateOnly(2026, 5, 2), new DateOnly(2026, 5, 31)), (window.FirstDay, window.LastDay));
        Assert.Equal(Utc(2026, 5, 1, 15), window.StartUtc); // local midnight May 2 = 15:00 UTC May 1
        Assert.Equal(Utc(2026, 5, 31, 15), window.EndUtc);
        Assert.Equal("Asia/Tokyo", window.TimeZoneId);
    }

    [Fact]
    public void LateInTheEveningUtc_ItIsAlreadyTomorrowInTokyo()
    {
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

        var window = InsightWindows.TrailingCompleteDays(Utc(2026, 6, 1, 20), tokyo, 30); // 05:00 on June 2 in Tokyo

        Assert.Equal(new DateOnly(2026, 6, 1), window.LastDay);
        Assert.Equal(Utc(2026, 5, 2, 15), window.StartUtc); // local midnight May 3 in Tokyo = 15:00 UTC May 2
        Assert.Equal(Utc(2026, 6, 1, 15), window.EndUtc);
    }

    [Fact]
    public void WithDaylightSaving_TheWindowStartsAtTheLocalMidnightOfEachEnd()
    {
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); // BST (+01:00) in summer

        // 2026-06-01 23:30 UTC is 00:30 on June 2 in London; today is June 2.
        var window = InsightWindows.TrailingCompleteDays(Utc(2026, 6, 1, 23, 30), london, 30);

        Assert.Equal((new DateOnly(2026, 5, 3), new DateOnly(2026, 6, 1)), (window.FirstDay, window.LastDay));
        Assert.Equal(Utc(2026, 5, 2, 23), window.StartUtc); // local midnight May 3 BST = 23:00 UTC May 2
        Assert.Equal(Utc(2026, 6, 1, 23), window.EndUtc);
    }

    [Fact]
    public void AWindowAcrossAClockChange_IsStillWholeLocalDays_SoItIsNotExactly24HoursPerDay()
    {
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); // clocks went forward on 2026-03-29

        var window = InsightWindows.TrailingCompleteDays(Utc(2026, 4, 5, 12), london, 14);

        Assert.Equal((new DateOnly(2026, 3, 22), new DateOnly(2026, 4, 4)), (window.FirstDay, window.LastDay));
        Assert.Equal(TimeSpan.FromDays(14) - TimeSpan.FromHours(1), window.EndUtc - window.StartUtc);
    }

    // A zone that is +02:00 in winter and +03:00 in summer, whose clocks jump from 00:00 to 01:00 on 10 April (so midnight
    // does not exist that day) and go back from 01:00 to 00:00 on 10 October (so midnight happens twice).
    private static readonly TimeZoneInfo Odd = TimeZoneInfo.CreateCustomTimeZone(
        "Test/MidnightChange",
        TimeSpan.FromHours(2),
        "Test",
        "Test standard",
        "Test daylight",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2026, 1, 1),
                new DateTime(2026, 12, 31),
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 4, 10),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 10, 10)),
        ]);

    [Fact]
    public void AMidnightThatDoesNotExist_StartsTheDayWhenTheClocksMoveForward()
    {
        // The day starting 10 April: local 00:00 does not exist, the day begins at 01:00 (+03:00) = 22:00 UTC on 9 April.
        var window = InsightWindows.TrailingCompleteDays(Utc(2026, 4, 10, 12), Odd, 5);

        Assert.Equal(new DateOnly(2026, 4, 10).AddDays(-1), window.LastDay);
        Assert.Equal(new DateTimeOffset(2026, 4, 10, 1, 0, 0, TimeSpan.FromHours(3)).ToUniversalTime(), window.EndUtc);
    }

    [Fact]
    public void AMidnightThatHappensTwice_StartsTheDayAtItsFirstOccurrence()
    {
        // The day starting 10 October: local 00:00 happens at +03:00 (21:00 UTC on 9 October) and again at +02:00.
        var window = InsightWindows.TrailingCompleteDays(Utc(2026, 10, 10, 12), Odd, 5);

        Assert.Equal(new DateTimeOffset(2026, 10, 9, 21, 0, 0, TimeSpan.Zero), window.EndUtc);
    }

    [Fact]
    public void ThePrecedingWindow_IsTheSameNumberOfDays_EndingWhereTheCurrentOneStarts()
    {
        var current = InsightWindows.TrailingCompleteDays(Utc(2026, 6, 1, 10), TimeZoneInfo.Utc, 30);

        var previous = InsightWindows.Preceding(current, TimeZoneInfo.Utc);

        Assert.Equal((new DateOnly(2026, 4, 2), new DateOnly(2026, 5, 1), 30), (previous.FirstDay, previous.LastDay, previous.Days));
        Assert.Equal(Utc(2026, 4, 2), previous.StartUtc);
        Assert.Equal(current.StartUtc, previous.EndUtc);
        Assert.Equal(current.TimeZoneId, previous.TimeZoneId);
    }

    [Fact]
    public void ThePrecedingWindow_FollowsTheOrganizationsTimeZone_AndLeavesNoGapAcrossAClockChange()
    {
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); // clocks went forward on 2026-03-29
        var current = InsightWindows.TrailingCompleteDays(Utc(2026, 4, 10, 12), london, 7);

        var previous = InsightWindows.Preceding(current, london);

        Assert.Equal((new DateOnly(2026, 3, 27), new DateOnly(2026, 4, 2)), (previous.FirstDay, previous.LastDay));
        Assert.Equal(current.StartUtc, previous.EndUtc);
        Assert.Equal(Utc(2026, 3, 27), previous.StartUtc); // local midnight March 27, still GMT
        Assert.Equal(TimeSpan.FromDays(7) - TimeSpan.FromHours(1), previous.EndUtc - previous.StartUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AWindowOfFewerThanOneDay_IsRejected(int days)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => InsightWindows.TrailingCompleteDays(Utc(2026, 6, 1), TimeZoneInfo.Utc, days));
    }
}
