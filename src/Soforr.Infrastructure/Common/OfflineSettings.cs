using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using Soforr.Application.Offline;
using Soforr.Domain.Offline;

namespace Soforr.Infrastructure.Common;

/// <summary>
/// The "Offline" configuration section (ADR 0007, 0008). Every value has a safe default and a safe range, so a nonsensical value stops
/// the application at start instead of silently weakening the boundary. The offline window is 72 hours by default and can be set
/// from 1 hour to 7 days.
/// </summary>
public sealed class OfflineOptions
{
    public const string SectionName = "Offline";

    /// <summary>How long after the last contact an offline event time can still be vouched for (policy: 72 hours).</summary>
    [Range(typeof(TimeSpan), "01:00:00", "7.00:00:00")]
    public TimeSpan Window { get; set; } = TimeSpan.FromHours(72);

    /// <summary>The shortest silence between two contacts that counts as a gap. At least twice the contact interval, so a missed contact is not a gap.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "01:00:00")]
    public TimeSpan MinGap { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The allowance for a device clock that runs ahead of the server's (only separates "in the future" from the other refusals; it never extends a gap).</summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:10:00")]
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How often the client is asked to report contact.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "00:30:00")]
    public TimeSpan ContactInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The longest one grant lives, whatever contact there is.</summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "90.00:00:00")]
    public TimeSpan GrantMaxAge { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How long a grant's evidence is kept after the last time it could vouch for an event.</summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "365.00:00:00")]
    public TimeSpan EvidenceRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Whether the relations between the values hold (single ranges cannot express them).</summary>
    public bool IsConsistent => MinGap >= ContactInterval * 2 && GrantMaxAge >= Window;
}

internal sealed class OfflineSettings(IOptions<OfflineOptions> options) : IOfflineSettings
{
    public OfflinePolicy Policy => new(options.Value.Window, options.Value.MinGap, options.Value.ClockSkew);

    public TimeSpan GrantMaxAge => options.Value.GrantMaxAge;

    public TimeSpan EvidenceRetention => options.Value.EvidenceRetention;

    public TimeSpan ContactInterval => options.Value.ContactInterval;
}
