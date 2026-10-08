using Soforr.Api.Http;
using Soforr.Api.Identity;
using Soforr.Application.Common;
using Soforr.Application.Sales;
using Soforr.Application.Sales.CreateSale;
using Soforr.Application.Sales.GetSale;
using Soforr.Application.Sales.ListSales;
using Soforr.Application.Sales.RecordPayment;
using Soforr.Application.Sales.ReversePayment;
using Soforr.Application.Sales.VoidSale;

namespace Soforr.Api.Sales;

/// <summary>
/// The Sales endpoints. They only translate HTTP to a command or query and a result to a response; the sale rules, the
/// permission checks and the tenant scoping are in the Domain and the handlers. Signing in is required (default deny)
/// and unsafe requests need an antiforgery token. No request carries a price, a currency or a total: the sale uses the
/// catalog price in the organization's currency and its totals are computed. Stock changes only through the sale and its void.
/// </summary>
public static class SalesEndpoints
{
    private const string BasePath = "/api/v1/sales";

    public static IEndpointRouteBuilder MapSalesEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup(BasePath).WithTags("Sales").RequireAntiforgery();

        group.MapPost(
                "/",
                async (SaleRequest request, ICommandHandler<CreateSaleCommand, SaleResultDto> handler, CancellationToken cancellationToken) =>
                {
                    var lines = request.Lines?.Select(line => new CreateSaleLine(line?.ProductId, line?.Quantity)).ToList();
                    var result = await handler.HandleAsync(new CreateSaleCommand(request.SaleId, lines, request.CustomerId, request.AmountPaid, request.PaymentMethod), cancellationToken);
                    return result.ToHttpResult(sale => sale.AlreadyRecorded ? Results.Ok(sale) : Results.Created($"{BasePath}/{sale.SaleId}", sale));
                })
            .WithName("CreateSale")
            .WithSummary("Records a sale at the catalog prices and removes the stock, atomically. Prices, costs and totals are taken and computed by the server. With a customerId it is a sale on account: amountPaid is what the customer pays now (0 up to the total; absent means paid in full), the rest is owed from a due date fixed now (sale date plus the customer's credit terms, or immediately). Without a customer the sale is paid in full. Sending the same saleId again with exactly the same products, quantities, customer and amount paid changes nothing and answers 200 with alreadyRecorded true; the same saleId with anything different is 409 (sale.id_conflict). Selling more than is in stock is 422.")
            .Produces<SaleResultDto>(StatusCodes.Status201Created)
            .Produces<SaleResultDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet(
                "/",
                async (string? status, int? page, int? pageSize, IQueryHandler<ListSalesQuery, PagedResult<SaleSummaryDto>> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new ListSalesQuery(status, page ?? 1, pageSize ?? ListSalesQuery.DefaultPageSize), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("ListSales")
            .WithSummary("Lists sales, newest first, optionally of one status (page size 1 to 100, default 25).")
            .Produces<PagedResult<SaleSummaryDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet(
                "/{id:guid}",
                async (Guid id, IQueryHandler<GetSaleQuery, SaleDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new GetSaleQuery(id), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("GetSale")
            .WithSummary("Gets a sale with its lines. Cost prices are shown only to callers who may see costs.")
            .Produces<SaleDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost(
                "/{id:guid}/void",
                async (Guid id, VoidSaleRequest request, ICommandHandler<VoidSaleCommand, SaleDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new VoidSaleCommand(id, request.Reason), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("VoidSale")
            .WithSummary("Voids a completed sale with a required reason and returns its stock, atomically. A sale can be voided once (422 otherwise). Owner and Manager only.")
            .Produces<SaleDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost(
                "/{id:guid}/payments",
                async (Guid id, PaymentRequest request, ICommandHandler<RecordPaymentCommand, PaymentResultDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new RecordPaymentCommand(id, request.PaymentId, request.Amount, request.PaidAtUtc, request.Method), cancellationToken);
                    return result.ToHttpResult(payment => payment.AlreadyRecorded ? Results.Ok(payment) : Results.Created($"{BasePath}/{id}", payment));
                })
            .WithName("RecordPayment")
            .WithSummary("Records a payment received against a sale made on account. Money is recorded, never processed. The amount cannot be more than what is still owed (422). paidAtUtc is when the money was received (default now; not before the sale, not in the future). Sending the same paymentId again with exactly the same sale, amount, method and date changes nothing and answers 200 with alreadyRecorded true; the same paymentId with anything different is 409 (sale.payment_id_conflict).")
            .Produces<PaymentResultDto>(StatusCodes.Status201Created)
            .Produces<PaymentResultDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost(
                "/{id:guid}/payments/{paymentId:guid}/reverse",
                async (Guid id, Guid paymentId, ReversalRequest request, ICommandHandler<ReversePaymentCommand, PaymentResultDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new ReversePaymentCommand(id, paymentId, request.ReversalId, request.Reason), cancellationToken);
                    return result.ToHttpResult(reversal => reversal.AlreadyRecorded ? Results.Ok(reversal) : Results.Created($"{BasePath}/{id}", reversal));
                })
            .WithName("ReversePayment")
            .WithSummary("Takes back one recorded payment in full, as a new record that points at it (the payment is never changed). A reason is required. A payment can be reversed once and a reversal cannot be reversed (422). Sending the same reversalId again for the same payment and reason answers 200 with alreadyRecorded true. Owner and Manager only.")
            .Produces<PaymentResultDto>(StatusCodes.Status201Created)
            .Produces<PaymentResultDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return app;
    }
}

/// <summary>One line of a sale request. There is no price: the catalog price is used.</summary>
public sealed record SaleLineRequest(Guid? ProductId, decimal? Quantity);

/// <summary>The request body of <c>POST /api/v1/sales</c>.</summary>
public sealed record SaleRequest(Guid? SaleId, IReadOnlyList<SaleLineRequest?>? Lines, Guid? CustomerId = null, decimal? AmountPaid = null, string? PaymentMethod = null);

/// <summary>The request body of <c>POST /api/v1/sales/{id}/void</c>.</summary>
public sealed record VoidSaleRequest(string? Reason);

/// <summary>The request body of <c>POST /api/v1/sales/{id}/payments</c>.</summary>
public sealed record PaymentRequest(Guid? PaymentId, decimal? Amount, DateTimeOffset? PaidAtUtc, string? Method);

/// <summary>The request body of <c>POST /api/v1/sales/{id}/payments/{paymentId}/reverse</c>.</summary>
public sealed record ReversalRequest(Guid? ReversalId, string? Reason);
