using Soforr.Api.Http;
using Soforr.Api.Identity;
using Soforr.Application.Common;
using Soforr.Application.Organizations;
using Soforr.Application.Organizations.GetOrganization;
using Soforr.Application.Organizations.UpdateOrganizationSettings;

namespace Soforr.Api.Organizations;

/// <summary>
/// The Organization endpoints. They only translate HTTP to a command or query and a result to a response; the rules
/// live in the Domain and the handlers. Signing in is required (default-deny), each handler checks its permission,
/// and an organization other than the caller's is not found (ADR 0002). Organizations are created by registering
/// (<c>POST /api/v1/auth/register</c>), so there is no create endpoint.
/// </summary>
public static class OrganizationEndpoints
{
    private const string BasePath = "/api/v1/organizations";

    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup(BasePath).WithTags("Organizations").RequireAntiforgery();

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
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut(
                "/{id:guid}/settings",
                async (Guid id, UpdateOrganizationSettingsRequest request, ICommandHandler<UpdateOrganizationSettingsCommand, OrganizationDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new UpdateOrganizationSettingsCommand(id, request.Name, request.TimeZoneId, request.OverdueMaterialityThreshold), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("UpdateOrganizationSettings")
            .WithSummary("Replaces an organization's settings: name, time zone and the optional overdue materiality threshold (the smallest overdue customer balance, in the organization's currency, worth an insight; absent clears it and then every overdue balance is reported). The currency cannot be changed.")
            .Produces<OrganizationDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}

/// <summary>The request body of <c>PUT /api/v1/organizations/{id}/settings</c>.</summary>
public sealed record UpdateOrganizationSettingsRequest(string? Name, string? TimeZoneId, decimal? OverdueMaterialityThreshold = null);
