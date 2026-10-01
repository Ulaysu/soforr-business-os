using System.Globalization;

namespace Soforr.Infrastructure.Identity;

/// <summary>
/// The absolute session lifetime. The cookie middleware only slides the idle timeout, so the moment the session
/// started is stored in the sign-in ticket (which sliding renewal and security-stamp refresh both preserve) and
/// checked on every request.
/// </summary>
internal static class SessionPolicy
{
    public const string SessionStartKey = "soforr.session_start";

    public static string Format(DateTimeOffset start) => start.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// A session is expired when it has no readable start (it did not come from our sign-in) or when it has lasted
    /// longer than <paramref name="absoluteLifetime"/>. Anything unreadable fails closed.
    /// </summary>
    public static bool IsExpired(string? sessionStart, DateTimeOffset now, TimeSpan absoluteLifetime) =>
        !DateTimeOffset.TryParse(sessionStart, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var start)
        || now - start >= absoluteLifetime;
}
