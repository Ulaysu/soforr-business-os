using Soforr.Api.Http;
using Soforr.Application.Common;
using Soforr.Application.Organizations;
using Soforr.Application.Organizations.CreateOrganization;
using Soforr.Application.Organizations.GetOrganization;
using Soforr.Application.Organizations.UpdateOrganizationSettings;

namespace Soforr.Api.Organizations;

/// <summary>
/// The Organization endpoints. They only translate HTTP to a command or query and a result to a response; the rules
/// live in the Domain and the handlers. Authentication and authorization are not in place yet, so these endpoints
/// are mapped only in Development (see Program.cs) until IDENTITY-002.
/// </summary>
public static class OrganizationEndpoints
{
    private const string BasePath = "/api/v1/organizations";

    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup(BasePath).WithTags("Organizations");

        group.MapPost(
                "/",
                async (CreateOrganizationRequest request, ICommandHandler<CreateOrganizationCommand, OrganizationDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new CreateOrganizationCommand(request.Name, request.Currency, request.TimeZoneId), cancellationToken);
                    return result.ToHttpResult(organization => Results.Created($"{BasePath}/{organization.Id}", organization));
                })
            .WithName("CreateOrganization")
            .WithSummary("Creates an organization.")
            .Produces<OrganizationDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        group.MapGet(
                "/{id:guid}",
                async (Guid id, IQueryHandler<GetOrganizationQuery, OrganizationDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new GetOrganizationQuery(id), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("GetOrganization")
            .WithSummary("Gets an organization.")
            .Produces<OrganizationDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut(
                "/{id:guid}/settings",
                async (Guid id, UpdateOrganizationSettingsRequest request, ICommandHandler<UpdateOrganizationSettingsCommand, OrganizationDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new UpdateOrganizationSettingsCommand(id, request.Name, request.TimeZoneId), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("UpdateOrganizationSettings")
            .WithSummary("Changes an organization's name and time zone. The currency cannot be changed.")
            .Produces<OrganizationDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}

/// <summary>The request body of <c>POST /api/v1/organizations</c>.</summary>
public sealed record CreateOrganizationRequest(string? Name, string? Currency, string? TimeZoneId);

/// <summary>The request body of <c>PUT /api/v1/organizations/{id}/settings</c>.</summary>
public sealed record UpdateOrganizationSettingsRequest(string? Name, string? TimeZoneId);
