namespace Soforr.Application.Audit;

/// <summary>
/// The correlation ID of the current request, so an audit event can point at the log lines of the request that caused
/// it. Null outside a request. The Api implements it from its correlation middleware; without one the default is null.
/// </summary>
public interface ICorrelationContext
{
    string? CorrelationId { get; }
}
