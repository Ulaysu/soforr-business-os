using Soforr.Application.Common;

namespace Soforr.Infrastructure.Common;

internal sealed class SystemClock : IClock
{
    /// <summary>
    /// The current UTC time, truncated to whole microseconds. PostgreSQL stores timestamps with microsecond
    /// precision, so a finer value would read back slightly different from the one that was returned when it was
    /// created.
    /// </summary>
    public DateTimeOffset UtcNow
    {
        get
        {
            var now = TimeProvider.System.GetUtcNow();
            return new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerMicrosecond), TimeSpan.Zero);
        }
    }
}
