using Soforr.Api.Http;
using Soforr.Application.Common;
using Soforr.Application.Intelligence.GetSalesSummary;

namespace Soforr.Api.Intelligence;

/// <summary>
/// The Intelligence endpoints. They only translate HTTP to a query and a result to a response; the formulas, the periods, the
/// permission check and the tenant scoping are in the Application layer. Signing in is required (default deny). Read-only.
/// </summary>
public static class IntelligenceEndpoints
{
    private const string BasePath = "/api/v1/intelligence";

    public static IEndpointRouteBuilder MapIntelligenceEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup(BasePath).WithTags("Intelligence");

        group.MapGet(
                "/sales-summary",
                async (int? days, int? top, IQueryHandler<GetSalesSummaryQuery, SalesSummaryDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new GetSalesSummaryQuery(days, top), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("GetSalesSummary")
            .WithSummary("Revenue and quantity for the last N complete local days (default 30, at most 92) against the N days before them, with top sellers, slow movers and declining products. Voided sales are excluded. Owner and Manager only.")
            .Produces<SalesSummaryDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }
}
