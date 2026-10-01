using Soforr.Application.Common;

namespace Soforr.Infrastructure.Common;

internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => TimeProvider.System.GetUtcNow();
}
