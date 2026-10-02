using Soforr.Api.Http;
using Soforr.Api.Identity;
using Soforr.Application.Catalog;
using Soforr.Application.Catalog.CreateProduct;
using Soforr.Application.Catalog.DeactivateProduct;
using Soforr.Application.Catalog.GetProduct;
using Soforr.Application.Catalog.SearchProducts;
using Soforr.Application.Catalog.UpdateProduct;
using Soforr.Application.Common;

namespace Soforr.Api.Catalog;

/// <summary>
/// The Product endpoints. They only translate HTTP to a command or query and a result to a response; the rules live
/// in the Domain and the handlers, which also check the caller's permission and organization (ADR 0002). Signing in
/// is required (default deny) and unsafe requests need an antiforgery token.
/// </summary>
public static class ProductEndpoints
{
    private const string BasePath = "/api/v1/products";

    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup(BasePath).WithTags("Products").RequireAntiforgery();

        group.MapPost(
                "/",
                async (ProductRequest request, ICommandHandler<CreateProductCommand, ProductDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(
                        new CreateProductCommand(request.Sku, request.Name, request.UnitOfMeasure, request.SellingPrice, request.CostPrice, request.ReorderPoint, request.ReorderLeadTimeDays),
                        cancellationToken);
                    return result.ToHttpResult(product => Results.Created($"{BasePath}/{product.Id}", product));
                })
            .WithName("CreateProduct")
            .WithSummary("Creates a product. Prices are in the organization's currency. A SKU that exists in any letter case is a conflict.")
            .Produces<ProductDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet(
                "/",
                async (
                    string? q,
                    bool? includeInactive,
                    int? page,
                    int? pageSize,
                    IQueryHandler<SearchProductsQuery, PagedResult<ProductDto>> handler,
                    CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(
                        new SearchProductsQuery(q, includeInactive ?? false, page ?? 1, pageSize ?? SearchProductsQuery.DefaultPageSize),
                        cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("SearchProducts")
            .WithSummary("Lists products, one page at a time (page size 1 to 100, default 25), optionally matching part of the SKU or name. Inactive products are left out unless includeInactive is true.")
            .Produces<PagedResult<ProductDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet(
                "/{id:guid}",
                async (Guid id, IQueryHandler<GetProductQuery, ProductDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new GetProductQuery(id), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("GetProduct")
            .WithSummary("Gets a product. The cost price is null for callers who may not see costs.")
            .Produces<ProductDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut(
                "/{id:guid}",
                async (Guid id, ProductRequest request, ICommandHandler<UpdateProductCommand, ProductDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(
                        new UpdateProductCommand(id, request.Sku, request.Name, request.UnitOfMeasure, request.SellingPrice, request.CostPrice, request.ReorderPoint, request.ReorderLeadTimeDays),
                        cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("UpdateProduct")
            .WithSummary("Replaces a product's details.")
            .Produces<ProductDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost(
                "/{id:guid}/deactivate",
                async (Guid id, ICommandHandler<DeactivateProductCommand, ProductDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new DeactivateProductCommand(id), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("DeactivateProduct")
            .WithSummary("Takes a product out of use. Products are never deleted. Deactivating an inactive product changes nothing.")
            .Produces<ProductDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}

/// <summary>The request body of <c>POST /api/v1/products</c> and <c>PUT /api/v1/products/{id}</c>. Prices are amounts; the currency is the organization's.</summary>
public sealed record ProductRequest(
    string? Sku,
    string? Name,
    string? UnitOfMeasure,
    decimal? SellingPrice,
    decimal? CostPrice,
    decimal? ReorderPoint,
    int? ReorderLeadTimeDays);
