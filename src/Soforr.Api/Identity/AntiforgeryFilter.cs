using Microsoft.AspNetCore.Antiforgery;

namespace Soforr.Api.Identity;

/// <summary>
/// Requires a valid antiforgery token on an unsafe request (ADR 0002). The cookie session is sent by the browser
/// automatically, so without this a page on another site could make the signed-in user's browser change data
/// (cross-site request forgery). The SPA fetches a token from <c>GET /api/v1/auth/antiforgery</c> and sends it in the
/// header named in the response; the matching antiforgery cookie is set by that same call.
/// </summary>
internal sealed class AntiforgeryFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

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
