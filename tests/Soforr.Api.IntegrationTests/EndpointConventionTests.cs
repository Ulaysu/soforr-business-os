using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Soforr.Api.Identity;

namespace Soforr.Api.IntegrationTests;

/// <summary>
/// Conventions that every endpoint, including future ones, must keep (ADR 0002). They read the real endpoint table
/// and need no database, so a new endpoint that forgets protection fails here.
/// </summary>
public class EndpointConventionTests
{
    // The only endpoints reachable without signing in. Adding one is a security decision: it must be added here, in a
    // reviewed change.
    private static readonly string[] AnonymousEndpoints =
    [
        "GET /api/v1/auth/antiforgery",
        "POST /api/v1/auth/register",
        "POST /api/v1/auth/login",
        "POST /api/v1/auth/logout",
        "GET /api/v1/auth/me",
        "GET /openapi/{documentName}.json",
        // The API reference page (Scalar), mapped in Development only, with the three static files it serves.
        "GET /scalar/{documentName?}",
        "GET /scalar/scalar.js",
        "GET /scalar/scalar.aspnetcore.js",
        "GET /scalar/favicon.svg",
        "ANY /health/live",
        "ANY /health/ready",
    ];

    // Answers requests that match no endpoint with 404 (see Program.cs).
    private const string CatchAllPattern = "/{**path}";

    private static readonly string[] SafeMethods = ["GET", "HEAD", "OPTIONS", "TRACE"];

    private static RouteEndpoint[] Endpoints()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        return factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
    }

    // An endpoint with no method restriction (the health checks) is listed as ANY.
    private static IEnumerable<(string Method, RouteEndpoint Endpoint)> ByMethod(IEnumerable<RouteEndpoint> endpoints) =>
        endpoints.SelectMany(endpoint =>
            endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods is { Count: > 0 } methods
                ? methods.Select(method => (method, endpoint))
                : [("ANY", endpoint)]);

    [Fact]
    public async Task TheFallbackPolicy_DeniesAnonymousCallers()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        var provider = factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        var fallback = await provider.GetFallbackPolicyAsync();

        Assert.NotNull(fallback);
        Assert.Contains(fallback.Requirements, requirement => requirement is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public void OnlyTheListedEndpoints_AllowAnonymousCallers()
    {
        var anonymous = ByMethod(Endpoints())
            .Where(entry => entry.Endpoint.RoutePattern.RawText != CatchAllPattern)
            .Where(entry => entry.Endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(entry => $"{entry.Method} {entry.Endpoint.RoutePattern.RawText}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(AnonymousEndpoints.Order(StringComparer.Ordinal).ToArray(), anonymous);
    }

    [Fact]
    public void EveryUnsafeEndpoint_RequiresAnAntiforgeryToken()
    {
        var unprotected = ByMethod(Endpoints())
            .Where(entry => entry.Endpoint.RoutePattern.RawText != CatchAllPattern)
            .Where(entry => entry.Method != "ANY") // checked separately below
            .Where(entry => !SafeMethods.Contains(entry.Method, StringComparer.OrdinalIgnoreCase))
            .Where(entry => entry.Endpoint.Metadata.GetMetadata<RequiresAntiforgeryMetadata>() is null)
            .Select(entry => $"{entry.Method} {entry.Endpoint.RoutePattern.RawText}")
            .ToArray();

        Assert.Empty(unprotected);
    }

    [Fact]
    public void OnlyTheHealthChecks_AcceptEveryMethod_BecauseTheyChangeNothing()
    {
        var anyMethod = ByMethod(Endpoints())
            .Where(entry => entry.Endpoint.RoutePattern.RawText != CatchAllPattern)
            .Where(entry => entry.Method == "ANY")
            .Select(entry => entry.Endpoint.RoutePattern.RawText!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["/health/live", "/health/ready"], anyMethod);
    }

    [Fact]
    public void TheEndpointTable_IsNotEmpty_SoTheConventionsAboveCheckSomething()
    {
        var all = ByMethod(Endpoints()).ToArray();

        Assert.Contains(all, entry => entry.Method == "PUT");
        Assert.Contains(all, entry => entry.Method == "POST");
    }
}
