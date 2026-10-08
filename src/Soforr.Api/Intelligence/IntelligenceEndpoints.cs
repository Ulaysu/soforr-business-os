using Soforr.Api.Http;
using Soforr.Application.Common;
using Soforr.Application.Intelligence.GetMarginSummary;
using Soforr.Application.Intelligence.GetPurchaseCosts;
using Soforr.Application.Intelligence.GetReceivables;
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

        group.MapGet(
                "/margins",
                async (int? days, int? top, IQueryHandler<GetMarginSummaryQuery, MarginSummaryDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new GetMarginSummaryQuery(days, top), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("GetMarginSummary")
            .WithSummary("Gross margin (revenue minus the cost price recorded on each sale line) for the last N complete local days (default 30, at most 92) against the N days before them, in total and per product: margin percentage, the change in percentage points, average price and cost, products at a loss, products whose margin fell, and products with no recorded cost (their margin is not guessed). A management measure, not accounting. Needs InsightsView and ProductViewCost (Owner and Manager).")
            .Produces<MarginSummaryDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet(
                "/purchase-costs",
                async (int? days, int? top, IQueryHandler<GetPurchaseCostsQuery, PurchaseCostsDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new GetPurchaseCostsQuery(days, top), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("GetPurchaseCosts")
            .WithSummary("Purchase unit costs from placed purchase orders: per product and supplier, the latest cost against the supplier's previous purchase (a change of at least 10% is notable), the gap between suppliers, and the volume (orders, quantity, spend) of the last N complete local days (default 30, at most 92). A single costed purchase is reported as insufficient history, never as no change. Needs InsightsView and ProductViewCost (Owner and Manager).")
            .Produces<PurchaseCostsDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet(
                "/receivables",
                async (int? top, IQueryHandler<GetReceivablesQuery, ReceivablesDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new GetReceivablesQuery(top), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("GetReceivables")
            .WithSummary("What customers owe, as of now: the total outstanding, the part not yet due and the overdue part split into aging buckets (0-30, 31-60, 61-90 and 91+ days past the due date, in the organization's calendar), and the most overdue customers with the sales behind each balance. A payment reduces only the sale it was recorded against. Derived from the sales and payment records on every request, never stored. Owner and Manager only.")
            .Produces<ReceivablesDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }
}
