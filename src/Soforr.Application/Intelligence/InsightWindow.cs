namespace Soforr.Application.Intelligence;

/// <summary>
/// A comparison period for a rule: <see cref="Days"/> whole local calendar days, from the start of <see cref="FirstDay"/>
/// (inclusive) to the start of the day after <see cref="LastDay"/> (exclusive), in the organization's time zone. The
/// instants are given in UTC so records can be compared directly: <c>StartUtc &lt;= time &lt; EndUtc</c>.
/// </summary>
public sealed record InsightWindow(DateOnly FirstDay, DateOnly LastDay, int Days, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string TimeZoneId);

public static class InsightWindows
{
    /// <summary>
    /// The <paramref name="days"/> complete local calendar days before today, where "today" is the day of
    /// <paramref name="nowUtc"/> in <paramref name="zone"/>. Today is left out on purpose: a partial day would make the
    /// result depend on the time of day it is evaluated, so the same data gives the same answer all day. A local midnight
    /// that does not exist (a clock change at midnight) starts at the first instant after the gap; one that happens twice
    /// starts at its first occurrence.
    /// </summary>
    public static InsightWindow TrailingCompleteDays(DateTimeOffset nowUtc, TimeZoneInfo zone, int days)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime);
        var first = today.AddDays(-days);
        var last = today.AddDays(-1);

        return new InsightWindow(first, last, days, LocalMidnightUtc(first, zone), LocalMidnightUtc(today, zone), zone.Id);
    }

    /// <summary>
    /// The period of equal length immediately before <paramref name="current"/>: the same number of whole local days, ending
    /// exactly where <paramref name="current"/> starts, so the two periods never overlap and leave no gap.
    /// </summary>
    public static InsightWindow Preceding(InsightWindow current, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(zone);

        var first = current.FirstDay.AddDays(-current.Days);
        var last = current.FirstDay.AddDays(-1);

        return new InsightWindow(first, last, current.Days, LocalMidnightUtc(first, zone), current.StartUtc, current.TimeZoneId);
    }

    private static DateTimeOffset LocalMidnightUtc(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(local))
        {
            // Midnight was skipped by a clock change: the day starts when the clocks move forward.
            local = local.AddHours(1);
        }

        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max() // the first occurrence has the larger offset
            : zone.GetUtcOffset(local);

        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
