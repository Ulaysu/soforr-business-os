using Soforr.Api.Http;
using Soforr.Application.Audit;
using Soforr.Application.Audit.ListAuditEvents;
using Soforr.Application.Common;

namespace Soforr.Api.Audit;

/// <summary>
/// The audit trail endpoint. It only translates HTTP to a query and a result to a response; the handler checks that
/// the caller may see the audit trail (Owner only) and scopes it to the caller's organization. There is no endpoint
/// that creates, changes or deletes an audit event: events are recorded by the use cases themselves.
/// </summary>
public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGroup("/api/v1/audit-events")
            .WithTags("Audit")
            .MapGet(
                "/",
                async (
                    string? action,
                    string? entityType,
                    Guid? entityId,
                    Guid? actorId,
                    DateTimeOffset? from,
                    DateTimeOffset? to,
                    int? page,
                    int? pageSize,
                    IQueryHandler<ListAuditEventsQuery, PagedResult<AuditEventDto>> handler,
                    CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(
                        new ListAuditEventsQuery(action, entityType, entityId, actorId, from, to, page ?? 1, pageSize ?? ListAuditEventsQuery.DefaultPageSize),
                        cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("ListAuditEvents")
            .WithSummary("Lists the organization's audit events, newest first, one page at a time (page size 1 to 100, default 25). Owner only. Filters are optional and exact; from is inclusive, to is exclusive.")
            .Produces<PagedResult<AuditEventDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }
}
