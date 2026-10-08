using Soforr.Api.Hosting;
using Soforr.Api.Http;
using Soforr.Api.Identity;
using Soforr.Application.Common;
using Soforr.Application.Offline;
using Soforr.Application.Offline.IssueOfflineGrant;
using Soforr.Application.Offline.RecordOfflineContact;

namespace Soforr.Api.Offline;

/// <summary>
/// The offline grant endpoints (ADR 0007, 0008). They only translate HTTP to a command and a result to a response. Signing in is
/// required (default deny), unsafe requests need an antiforgery token, and both endpoints share the offline rate limit of the API
/// hardening configuration. The caller's user and organization come from the session; the grant id in the path is an
/// <b>identifier, not a secret</b> and authorizes nothing without the session. Every time in a request or response that matters is the
/// server's.
/// </summary>
public static class OfflineEndpoints
{
    private const string BasePath = "/api/v1/offline/grants";

    public static IEndpointRouteBuilder MapOfflineEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup(BasePath).WithTags("Offline").RequireAntiforgery().RequireRateLimiting(ApiHardeningExtensions.OfflinePolicy);

        group.MapPost(
                "/",
                async (ICommandHandler<IssueOfflineGrantCommand, OfflineGrantDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new IssueOfflineGrantCommand(), cancellationToken);
                    return result.ToHttpResult(grant => Results.Created($"{BasePath}/{grant.GrantId}", grant));
                })
            .WithName("IssueOfflineGrant")
            .WithSummary("Issues the caller's offline grant after an online validation, replacing their previous one. The grant id is an identifier, not a secret: it does nothing without the caller's session. Needs the permission to record sales or payments; each offline sale and payment is still checked against its own permission when it is sent. The server generates the id and every time; the device clock is never used. The device should then report contact about every 5 minutes (POST .../{id}/contact) while it is online. Honest limit: the server cannot tell a disconnected device from one that stays silent on purpose; it vouches for an offline event time only inside a silent gap it observed.")
            .Produces<OfflineGrantDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost(
                "/{id:guid}/contact",
                async (Guid id, ICommandHandler<RecordOfflineContactCommand, OfflineGrantDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new RecordOfflineContactCommand(id), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("RecordOfflineContact")
            .WithSummary("Reports that the caller's device is in contact; the server records its own clock. 404 offline.grant_not_found (also for another user's or organization's grant), 409 offline.grant_revoked, offline.grant_superseded or offline.grant_expired (ask for a new grant). Contact is append-only: it never changes what earlier silent gaps were.")
            .Produces<OfflineGrantDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return app;
    }
}
