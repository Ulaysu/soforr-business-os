using Soforr.Application.Audit;

namespace Soforr.Infrastructure.Common;

/// <summary>The default <see cref="ICorrelationContext"/>: no correlation ID (a job, a test). The Api replaces it with one that reads the request's.</summary>
internal sealed class NoCorrelationContext : ICorrelationContext
{
    public string? CorrelationId => null;
}
