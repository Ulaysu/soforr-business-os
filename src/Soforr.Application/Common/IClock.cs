namespace Soforr.Application.Common;

/// <summary>The current time. Injected so time-dependent behavior is deterministic in tests.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
