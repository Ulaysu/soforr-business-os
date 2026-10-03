using Soforr.Api.Http;
using Soforr.Api.Identity;
using Soforr.Application.Common;
using Soforr.Application.Inventory.AdjustInventory;
using Soforr.Application.Inventory.GetMovementHistory;
using Soforr.Application.Inventory.GetStockLevel;
using Soforr.Application.Inventory.ListLowStock;

namespace Soforr.Api.Inventory;

/// <summary>
/// The Inventory endpoints. They only translate HTTP to a command or query and a result to a response; the stock
/// rules, the permission checks and the tenant scoping are in the handlers. Signing in is required (default deny) and
/// unsafe requests need an antiforgery token. Stock changes only through recorded movements: there is no endpoint that
/// sets a quantity, and no endpoint that changes or deletes a movement.
/// </summary>
public static class InventoryEndpoints
{
    private const string BasePath = "/api/v1/inventory";

    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup(BasePath).WithTags("Inventory").RequireAntiforgery();

        group.MapPost(
                "/adjustments",
                async (AdjustInventoryRequest request, ICommandHandler<AdjustInventoryCommand, AdjustmentResultDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new AdjustInventoryCommand(request.ProductId, request.QuantityChange, request.Note, request.AdjustmentId), cancellationToken);
                    return result.ToHttpResult(adjustment => adjustment.AlreadyRecorded ? Results.Ok(adjustment) : Results.Created($"{BasePath}/stock/{request.ProductId}/movements", adjustment));
                })
            .WithName("AdjustInventory")
            .WithSummary("Adjusts a product's stock by hand. A note is required. Sending the same adjustmentId again changes nothing and answers 200 with alreadyRecorded true. Removing more than exists is 422.")
            .Produces<AdjustmentResultDto>(StatusCodes.Status201Created)
            .Produces<AdjustmentResultDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet(
                "/stock/{productId:guid}",
                async (Guid productId, IQueryHandler<GetStockLevelQuery, StockLevelDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new GetStockLevelQuery(productId), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("GetStockLevel")
            .WithSummary("Gets a product's stock (0 if it has never had stock) and whether it is low.")
            .Produces<StockLevelDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet(
                "/low-stock",
                async (int? page, int? pageSize, IQueryHandler<ListLowStockQuery, PagedResult<LowStockItemDto>> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new ListLowStockQuery(page ?? 1, pageSize ?? ListLowStockQuery.DefaultPageSize), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("ListLowStock")
            .WithSummary("Lists active products with reorder settings whose stock is at or below the reorder point, the emptiest first (page size 1 to 100, default 25).")
            .Produces<PagedResult<LowStockItemDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet(
                "/stock/{productId:guid}/movements",
                async (Guid productId, int? page, int? pageSize, IQueryHandler<GetMovementHistoryQuery, PagedResult<MovementDto>> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new GetMovementHistoryQuery(productId, page ?? 1, pageSize ?? GetMovementHistoryQuery.DefaultPageSize), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("GetMovementHistory")
            .WithSummary("Lists a product's stock movements, newest first (page size 1 to 100, default 25). Owner and Manager only.")
            .Produces<PagedResult<MovementDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}

/// <summary>The request body of <c>POST /api/v1/inventory/adjustments</c>.</summary>
public sealed record AdjustInventoryRequest(Guid ProductId, decimal? QuantityChange, string? Note, Guid? AdjustmentId);
