using Microsoft.AspNetCore.Authorization;

namespace Soforr.Api.Identity;

/// <summary>Api-side security configuration. The sign-in cookie itself is configured in Infrastructure (ADR 0002).</summary>
public static class ApiSecurityExtensions
{
    public static IServiceCollection AddApiSecurity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "__Host-soforr.antiforgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
        });

        // Default deny: an endpoint without an explicit AllowAnonymous requires a signed-in user (ADR 0002).
        services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return services;
    }
}
