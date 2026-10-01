using System.ComponentModel.DataAnnotations;

namespace Soforr.Infrastructure.Identity;

/// <summary>
/// Lifetime of the sign-in cookie (ADR 0002). A session ends after <see cref="IdleTimeout"/> without activity
/// (sliding) and never lasts longer than <see cref="AbsoluteLifetime"/>, however active it is. The defaults are
/// conservative; all values can be overridden in the "Session" configuration section.
/// </summary>
public sealed class SessionOptions
{
    public const string SectionName = "Session";

    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromHours(1);

    [Range(typeof(TimeSpan), "00:00:01", "30.00:00:00")]
    public TimeSpan AbsoluteLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// How often a session is re-checked against the user's security stamp and membership. A password change, a
    /// role change or a deactivation takes effect for existing sessions within this interval (ADR 0002).
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan SecurityStampValidationInterval { get; set; } = TimeSpan.FromMinutes(1);
}
