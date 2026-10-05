using Soforr.Api.Http;
using Soforr.Api.Identity;
using Soforr.Application.Common;
using Soforr.Application.Purchasing;
using Soforr.Application.Purchasing.CancelPurchaseOrder;
using Soforr.Application.Purchasing.CreatePurchaseOrder;
using Soforr.Application.Purchasing.CreateSupplier;
using Soforr.Application.Purchasing.GetPurchaseOrder;
using Soforr.Application.Purchasing.ListPurchaseOrders;
using Soforr.Application.Purchasing.ListSuppliers;
using Soforr.Application.Purchasing.PlacePurchaseOrder;
using Soforr.Application.Purchasing.ReceivePurchase;

namespace Soforr.Api.Purchasing;

/// <summary>
/// The Purchasing endpoints. They only translate HTTP to a command or query and a result to a response; the order
/// rules, the permission checks and the tenant scoping are in the Domain and the handlers. Signing in is required
/// (default deny) and unsafe requests need an antiforgery token. No request carries a currency or a total: the order is
/// in the organization's currency and its totals are computed. Stock changes only through receiving.
/// </summary>
public static class PurchasingEndpoints
{
    private const string BasePath = "/api/v1/purchasing";

    public static IEndpointRouteBuilder MapPurchasingEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup(BasePath).WithTags("Purchasing").RequireAntiforgery();

        group.MapPost(
                "/suppliers",
                async (SupplierRequest request, ICommandHandler<CreateSupplierCommand, SupplierDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new CreateSupplierCommand(request.Name, request.ContactDetails), cancellationToken);
                    return result.ToHttpResult(supplier => Results.Created($"{BasePath}/suppliers/{supplier.Id}", supplier));
                })
            .WithName("CreateSupplier")
            .WithSummary("Creates a supplier.")
            .Produces<SupplierDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet(
                "/suppliers",
                async (bool? includeInactive, int? page, int? pageSize, IQueryHandler<ListSuppliersQuery, PagedResult<SupplierDto>> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new ListSuppliersQuery(includeInactive ?? false, page ?? 1, pageSize ?? ListSuppliersQuery.DefaultPageSize), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("ListSuppliers")
            .WithSummary("Lists suppliers by name (page size 1 to 100, default 25). Inactive suppliers are left out unless includeInactive is true.")
            .Produces<PagedResult<SupplierDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost(
                "/orders",
                async (PurchaseOrderRequest request, ICommandHandler<CreatePurchaseOrderCommand, PurchaseOrderDto> handler, CancellationToken cancellationToken) =>
                {
                    var lines = request.Lines?.Select(line => new CreatePurchaseOrderLine(line?.ProductId, line?.Quantity, line?.UnitCost)).ToList();
                    var result = await handler.HandleAsync(new CreatePurchaseOrderCommand(request.SupplierId, request.Reference, lines), cancellationToken);
                    return result.ToHttpResult(order => Results.Created($"{BasePath}/orders/{order.Id}", order));
                })
            .WithName("CreatePurchaseOrder")
            .WithSummary("Creates a draft purchase order with its lines. Unit costs are in the organization's currency, which the order takes; a currency or a total in the request is not read. The product's SKU, name and unit are recorded on the line.")
            .Produces<PurchaseOrderDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet(
                "/orders",
                async (string? status, Guid? supplierId, int? page, int? pageSize, IQueryHandler<ListPurchaseOrdersQuery, PagedResult<PurchaseOrderSummaryDto>> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new ListPurchaseOrdersQuery(status, supplierId, page ?? 1, pageSize ?? ListPurchaseOrdersQuery.DefaultPageSize), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("ListPurchaseOrders")
            .WithSummary("Lists purchase orders, newest first, optionally of one status or supplier (page size 1 to 100, default 25).")
            .Produces<PagedResult<PurchaseOrderSummaryDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet(
                "/orders/{id:guid}",
                async (Guid id, IQueryHandler<GetPurchaseOrderQuery, PurchaseOrderDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new GetPurchaseOrderQuery(id), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("GetPurchaseOrder")
            .WithSummary("Gets a purchase order with its lines and received quantities.")
            .Produces<PurchaseOrderDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost(
                "/orders/{id:guid}/place",
                async (Guid id, ICommandHandler<PlacePurchaseOrderCommand, PurchaseOrderDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new PlacePurchaseOrderCommand(id), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("PlacePurchaseOrder")
            .WithSummary("Places a draft order with the supplier. Only a draft can be placed (422 otherwise).")
            .Produces<PurchaseOrderDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost(
                "/orders/{id:guid}/cancel",
                async (Guid id, ICommandHandler<CancelPurchaseOrderCommand, PurchaseOrderDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new CancelPurchaseOrderCommand(id), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("CancelPurchaseOrder")
            .WithSummary("Cancels a draft or placed order that has received nothing (422 otherwise).")
            .Produces<PurchaseOrderDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost(
                "/orders/{id:guid}/receipts",
                async (Guid id, ReceiptRequest request, ICommandHandler<ReceivePurchaseCommand, ReceiptResultDto> handler, CancellationToken cancellationToken) =>
                {
                    var lines = request.Lines?.Select(line => new ReceivePurchaseLine(line?.LineId, line?.Quantity)).ToList();
                    var result = await handler.HandleAsync(new ReceivePurchaseCommand(id, request.ReceiptId, lines), cancellationToken);
                    return result.ToHttpResult(receipt => receipt.AlreadyRecorded ? Results.Ok(receipt) : Results.Created($"{BasePath}/orders/{id}", receipt));
                })
            .WithName("ReceivePurchase")
            .WithSummary("Records goods received against lines of a placed or partially received order and adds the stock, atomically. Sending the same receiptId again with exactly the same order, lines and quantities changes nothing and answers 200 with alreadyRecorded true; the same receiptId with anything different is 409 (purchase_order.receipt_id_conflict). Receiving more than is outstanding is 422.")
            .Produces<ReceiptResultDto>(StatusCodes.Status201Created)
            .Produces<ReceiptResultDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return app;
    }
}

/// <summary>The request body of <c>POST /api/v1/purchasing/suppliers</c>.</summary>
public sealed record SupplierRequest(string? Name, string? ContactDetails);

/// <summary>One line of a purchase order request. There is no currency: unit costs are in the organization's currency.</summary>
public sealed record PurchaseOrderLineRequest(Guid? ProductId, decimal? Quantity, decimal? UnitCost);

/// <summary>The request body of <c>POST /api/v1/purchasing/orders</c>.</summary>
public sealed record PurchaseOrderRequest(Guid? SupplierId, string? Reference, IReadOnlyList<PurchaseOrderLineRequest?>? Lines);

/// <summary>One line of a receipt: how much of an order line arrived.</summary>
public sealed record ReceiptLineRequest(Guid? LineId, decimal? Quantity);

/// <summary>The request body of <c>POST /api/v1/purchasing/orders/{id}/receipts</c>.</summary>
public sealed record ReceiptRequest(Guid? ReceiptId, IReadOnlyList<ReceiptLineRequest?>? Lines);
