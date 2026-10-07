using Soforr.Api.Http;
using Soforr.Api.Identity;
using Soforr.Application.Common;
using Soforr.Application.Customers;
using Soforr.Application.Customers.CreateCustomer;
using Soforr.Application.Customers.DeactivateCustomer;
using Soforr.Application.Customers.GetCustomer;
using Soforr.Application.Customers.SearchCustomers;
using Soforr.Application.Customers.UpdateCustomer;

namespace Soforr.Api.Customers;

/// <summary>
/// The Customer endpoints. They only translate HTTP to a command or query and a result to a response; the rules live in the
/// Domain and the handlers, which also check the caller's permission and organization (ADR 0002). Signing in is required
/// (default deny) and unsafe requests need an antiforgery token. There is no delete: a customer is deactivated.
/// </summary>
public static class CustomerEndpoints
{
    private const string BasePath = "/api/v1/customers";

    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup(BasePath).WithTags("Customers").RequireAntiforgery();

        group.MapPost(
                "/",
                async (CustomerRequest request, ICommandHandler<CreateCustomerCommand, CustomerDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new CreateCustomerCommand(request.Name, request.ContactDetails, request.CreditTermsDays), cancellationToken);
                    return result.ToHttpResult(customer => Results.Created($"{BasePath}/{customer.Id}", customer));
                })
            .WithName("CreateCustomer")
            .WithSummary("Creates a customer. Credit terms are the number of days until a balance is overdue (0 to 365), optional.")
            .Produces<CustomerDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet(
                "/",
                async (
                    string? q,
                    bool? includeInactive,
                    int? page,
                    int? pageSize,
                    IQueryHandler<SearchCustomersQuery, PagedResult<CustomerDto>> handler,
                    CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(
                        new SearchCustomersQuery(q, includeInactive ?? false, page ?? 1, pageSize ?? SearchCustomersQuery.DefaultPageSize),
                        cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("SearchCustomers")
            .WithSummary("Lists customers by name, one page at a time (page size 1 to 100, default 25), optionally matching part of the name. Inactive customers are left out unless includeInactive is true. Contact details are null for callers who may not manage customers.")
            .Produces<PagedResult<CustomerDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet(
                "/{id:guid}",
                async (Guid id, IQueryHandler<GetCustomerQuery, CustomerDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new GetCustomerQuery(id), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("GetCustomer")
            .WithSummary("Gets a customer. Contact details are null for callers who may not manage customers.")
            .Produces<CustomerDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut(
                "/{id:guid}",
                async (Guid id, CustomerRequest request, ICommandHandler<UpdateCustomerCommand, CustomerDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new UpdateCustomerCommand(id, request.Name, request.ContactDetails, request.CreditTermsDays), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("UpdateCustomer")
            .WithSummary("Replaces a customer's name, contact details and credit terms. An absent contact or credit terms clears it.")
            .Produces<CustomerDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost(
                "/{id:guid}/deactivate",
                async (Guid id, ICommandHandler<DeactivateCustomerCommand, CustomerDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new DeactivateCustomerCommand(id), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("DeactivateCustomer")
            .WithSummary("Takes a customer out of use. Customers are never deleted. Deactivating an inactive customer changes nothing.")
            .Produces<CustomerDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}

/// <summary>The request body of <c>POST /api/v1/customers</c> and <c>PUT /api/v1/customers/{id}</c>.</summary>
public sealed record CustomerRequest(string? Name, string? ContactDetails, int? CreditTermsDays);
