using Soforr.Application.Audit;

namespace Soforr.Api.Observability;

/// <summary>Gives the Application the correlation ID of the current request, so an audit event can point at its log lines.</summary>
internal sealed class HttpCorrelationContext(IHttpContextAccessor accessor) : ICorrelationContext
{
    public string? CorrelationId => accessor.HttpContext?.GetCorrelationId();
}
