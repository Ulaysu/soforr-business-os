using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using Soforr.Api.Http;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Identity.GetCurrentUser;
using Soforr.Application.Identity.Register;
using Soforr.Application.Identity.SignIn;

namespace Soforr.Api.Identity;

/// <summary>
/// The authentication endpoints (ADR 0002): register, sign in, sign out, who am I, and the antiforgery token.
/// They only translate HTTP to a command or query and a result to a response. Every unsafe request requires an
/// antiforgery token.
/// </summary>
public static class AuthEndpoints
{
    private const string BasePath = "/api/v1/auth";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // These endpoints are how anyone signs in, so they are the anonymous exceptions to the default-deny policy.
        var group = app.MapGroup(BasePath).WithTags("Authentication").AllowAnonymous().RequireAntiforgery();

        group.MapGet(
                "/antiforgery",
                (HttpContext context, IAntiforgery antiforgery, IOptions<AntiforgeryOptions> options) =>
                {
                    var tokens = antiforgery.GetAndStoreTokens(context);
                    context.Response.Headers.CacheControl = "no-store";
                    return Results.Ok(new AntiforgeryTokenResponse(options.Value.HeaderName!, tokens.RequestToken!));
                })
            .WithName("GetAntiforgeryToken")
            .WithSummary("Returns the antiforgery token to send with every unsafe request, and sets its cookie. Request a new token after signing in or out.")
            .Produces<AntiforgeryTokenResponse>();

        group.MapPost(
                "/register",
                async (RegisterRequest request, ICommandHandler<RegisterCommand, AccountDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(
                        new RegisterCommand(request.Email, request.Password, request.OrganizationName, request.Currency, request.TimeZoneId),
                        cancellationToken);
                    return result.ToHttpResult(account => Results.Created($"{BasePath}/me", account));
                })
            .WithName("Register")
            .WithSummary("Registers a user and a new organization that the user owns. The user must then sign in.")
            .Produces<AccountDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost(
                "/login",
                async (SignInRequest request, ICommandHandler<SignInCommand, AccountDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new SignInCommand(request.Email, request.Password), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("SignIn")
            .WithSummary("Signs in with e-mail and password and starts a cookie session.")
            .Produces<AccountDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost(
                "/logout",
                async (ISessionService sessions) =>
                {
                    await sessions.SignOutAsync();
                    return Results.NoContent();
                })
            .WithName("SignOut")
            .WithSummary("Ends the cookie session.")
            .Produces(StatusCodes.Status204NoContent);

        group.MapGet(
                "/me",
                async (IQueryHandler<GetCurrentUserQuery, AccountDto> handler, CancellationToken cancellationToken) =>
                {
                    var result = await handler.HandleAsync(new GetCurrentUserQuery(), cancellationToken);
                    return result.ToHttpResult(Results.Ok);
                })
            .WithName("GetCurrentUser")
            .WithSummary("Returns the signed-in user, their organization and role.")
            .Produces<AccountDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return app;
    }
}

public sealed record AntiforgeryTokenResponse(string HeaderName, string Token);

public sealed record RegisterRequest(string? Email, string? Password, string? OrganizationName, string? Currency, string? TimeZoneId);

public sealed record SignInRequest(string? Email, string? Password);
