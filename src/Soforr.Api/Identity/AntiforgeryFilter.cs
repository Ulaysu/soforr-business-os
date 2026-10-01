using Microsoft.AspNetCore.Antiforgery;

namespace Soforr.Api.Identity;

/// <summary>
/// Requires a valid antiforgery token on an unsafe request (ADR 0002). The cookie session is sent by the browser
/// automatically, so without this a page on another site could make the signed-in user's browser change data
/// (cross-site request forgery). The SPA fetches a token from <c>GET /api/v1/auth/antiforgery</c> and sends it in the
/// header named in the response; the matching antiforgery cookie is set by that same call. Safe methods (GET, HEAD,
/// OPTIONS, TRACE) change nothing and are not checked. Apply it with <see cref="AntiforgeryEndpointExtensions.RequireAntiforgery"/>.
/// </summary>
internal sealed class AntiforgeryFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (HttpMethods.IsGet(context.HttpContext.Request.Method)
            || HttpMethods.IsHead(context.HttpContext.Request.Method)
            || HttpMethods.IsOptions(context.HttpContext.Request.Method)
            || HttpMethods.IsTrace(context.HttpContext.Request.Method))
        {
            return await next(context);
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.Problem(
                "The antiforgery token is missing or invalid. Request a new one and try again.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Bad Request",
                extensions: new Dictionary<string, object?> { ["code"] = "antiforgery.invalid" });
        }

        return await next(context);
    }
}

/// <summary>Marks an endpoint as protected against cross-site request forgery. A convention test requires it on every unsafe endpoint.</summary>
public sealed class RequiresAntiforgeryMetadata;

public static class AntiforgeryEndpointExtensions
{
    /// <summary>Requires a valid antiforgery token on the unsafe requests of the endpoint or group.</summary>
    public static TBuilder RequireAntiforgery<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddEndpointFilterFactory((_, next) => context => new AntiforgeryFilter(
            context.HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>()).InvokeAsync(context, next))
            .WithMetadata(new RequiresAntiforgeryMetadata());
    }
}
